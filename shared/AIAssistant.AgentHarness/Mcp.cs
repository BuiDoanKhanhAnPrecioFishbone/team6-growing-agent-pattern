using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace AIAssistant.Harness;

/// <summary>
/// A real Model Context Protocol client over the stdio transport (Claude Code Ch 15). Spawns an MCP server
/// process, speaks JSON-RPC 2.0 over its stdin/stdout (newline-delimited), and exposes its tools. This is the
/// "let the operator add tools" seam: any MCP server (filesystem, git, a database, an internal API) becomes
/// callable by every agent, with no harness change. MCP tools are **gated by default** (they run through the
/// tool-loop's permit) unless the server hints a tool is read-only.
/// </summary>
public sealed class McpStdioClient : IAsyncDisposable
{
    private readonly Process _proc;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> _pending = new();
    private int _nextId;

    private McpStdioClient(Process proc) { _proc = proc; _ = ReadLoopAsync(); _ = DrainStderrAsync(); }

    // Drain stderr so a chatty server can't deadlock on a full stderr buffer (we don't parse it).
    private async Task DrainStderrAsync()
    {
        try { while (await _proc.StandardError.ReadLineAsync() is not null) { } } catch { /* closed */ }
    }

    /// <summary>Launch an MCP server (e.g. <c>npx -y @modelcontextprotocol/server-everything</c>) and handshake.</summary>
    public static async Task<McpStdioClient> StartAsync(string command, IEnumerable<string>? args = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(command)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in args ?? Array.Empty<string>()) psi.ArgumentList.Add(a);
        var proc = Process.Start(psi) ?? throw new InvalidOperationException($"could not start MCP server '{command}'");
        var client = new McpStdioClient(proc);
        await client.InitializeAsync(ct);
        return client;
    }

    private async Task ReadLoopAsync()
    {
        string? line;
        try
        {
            while ((line = await _proc.StandardOutput.ReadLineAsync()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonNode? node; try { node = JsonNode.Parse(line); } catch { continue; }
                if (node?["id"] is JsonValue v && v.TryGetValue<int>(out var id) && _pending.TryRemove(id, out var tcs))
                    tcs.TrySetResult(node);
            }
        }
        catch { /* server closed */ }
        foreach (var tcs in _pending.Values) tcs.TrySetException(new IOException("MCP server stream closed"));
    }

    private async Task<JsonNode?> RpcAsync(string method, JsonObject? prms, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (prms is not null) msg["params"] = prms;
        await _proc.StandardInput.WriteLineAsync(msg.ToJsonString());
        await _proc.StandardInput.FlushAsync();
        using (ct.Register(() => tcs.TrySetCanceled()))
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
    }

    private async Task NotifyAsync(string method)
    {
        await _proc.StandardInput.WriteLineAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method }.ToJsonString());
        await _proc.StandardInput.FlushAsync();
    }

    private async Task InitializeAsync(CancellationToken ct)
    {
        await RpcAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = "2024-11-05",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "growing-agent-harness", ["version"] = "0.1" },
        }, ct);
        await NotifyAsync("notifications/initialized");
    }

    public async Task<IReadOnlyList<JsonObject>> ListToolsAsync(CancellationToken ct = default)
    {
        var resp = await RpcAsync("tools/list", null, ct);
        return (resp?["result"]?["tools"] as JsonArray)?.OfType<JsonObject>()
               .Select(o => (JsonObject)o.DeepClone()!).ToList() ?? new List<JsonObject>();
    }

    public async Task<string> CallToolAsync(string name, JsonObject arguments, CancellationToken ct = default)
    {
        var resp = await RpcAsync("tools/call", new JsonObject { ["name"] = name, ["arguments"] = arguments.DeepClone() }, ct);
        if (resp?["error"] is JsonObject err) return "error: " + (err["message"]?.GetValue<string>() ?? "mcp error");
        if (resp?["result"]?["content"] is not JsonArray content) return resp?["result"]?.ToJsonString() ?? "";
        var text = string.Join("\n", content.OfType<JsonObject>()
            .Select(c => c["type"]?.GetValue<string>() == "text" ? c["text"]?.GetValue<string>() ?? "" : $"[{c["type"]?.GetValue<string>()} content]")
            .Where(s => s.Length > 0));
        return text.Length > 0 ? text : "(no text content)";
    }

    public async ValueTask DisposeAsync()
    {
        try { if (!_proc.HasExited) { _proc.StandardInput.Close(); _proc.Kill(entireProcessTree: true); } } catch { /* best effort */ }
        _proc.Dispose();
        await Task.CompletedTask;
    }
}

/// <summary>One tool discovered on an MCP server, adapted to <see cref="ITool"/>. Gated (ReadOnly=false) unless
/// the server's <c>annotations.readOnlyHint</c> says otherwise — so mutating/outward MCP tools hit the
/// tool-loop's permit before they run.</summary>
public sealed class McpTool : ITool
{
    private readonly McpStdioClient _client;
    private readonly JsonObject _def;
    public McpTool(McpStdioClient client, JsonObject def) { _client = client; _def = def; }

    public string Name => _def["name"]?.GetValue<string>() ?? "mcp_tool";
    public string Description => _def["description"]?.GetValue<string>() ?? "";
    public JsonObject Parameters =>
        (_def["inputSchema"] as JsonObject)?.DeepClone() as JsonObject
        ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
    public bool ReadOnly => (_def["annotations"]?["readOnlyHint"] as JsonValue)?.TryGetValue<bool>(out var ro) == true && ro;
    public Task<string> InvokeAsync(JsonObject args, CancellationToken ct = default) => _client.CallToolAsync(Name, args, ct);
}

/// <summary>Extensibility seam realized: connect to an MCP server and surface its tools as <see cref="ITool"/>s
/// for the agent's tool-loop. Dispose to shut the server down.</summary>
public sealed class McpToolSource : IToolSource, IAsyncDisposable
{
    private readonly McpStdioClient _client;
    public string Endpoint { get; }
    public IReadOnlyList<ITool> Tools { get; }

    private McpToolSource(string endpoint, McpStdioClient client, IReadOnlyList<ITool> tools)
    { Endpoint = endpoint; _client = client; Tools = tools; }

    /// <summary>Spawn a stdio MCP server, list its tools, wrap each as a gated <see cref="ITool"/>.</summary>
    public static async Task<McpToolSource> ConnectStdioAsync(string command, IEnumerable<string>? args = null, CancellationToken ct = default)
    {
        var client = await McpStdioClient.StartAsync(command, args, ct);
        var tools = (await client.ListToolsAsync(ct)).Select(d => (ITool)new McpTool(client, d)).ToList();
        return new McpToolSource($"stdio:{command}", client, tools);
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
