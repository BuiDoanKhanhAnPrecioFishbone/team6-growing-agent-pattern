using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace AIAssistant.Harness;

/// <summary>A capability the agent can call during generation (Memory v2, D8-10). Read-only tools run
/// freely; non-read-only (mutating/outward) tools go through the loop's permit gate — tools widen what the
/// agent KNOWS, the reward still governs what it OUTPUTS.</summary>
public interface ITool
{
    string Name { get; }
    string Description { get; }
    JsonObject Parameters { get; }   // JSON-schema for the function arguments
    bool ReadOnly { get; }
    Task<string> InvokeAsync(JsonObject args, CancellationToken ct = default);
}

/// <summary>Extensibility seam — a source of tools (built-ins, or an MCP server). See <see cref="McpToolSource"/>.</summary>
public interface IToolSource { IReadOnlyList<ITool> Tools { get; } }

/// <summary>Lets the agent query its own learned memory as a tool (read-only).</summary>
public sealed class MemorySearchTool : ITool
{
    private readonly ILessonStore _store; private readonly string _agent, _sector;
    public MemorySearchTool(ILessonStore store, string agent, string sector) { _store = store; _agent = agent; _sector = sector; }
    public string Name => "memory_search";
    public string Description => "Search your own learned lessons for advice relevant to a situation before you answer.";
    public bool ReadOnly => true;
    public JsonObject Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { ["query"] = new JsonObject { ["type"] = "string", ["description"] = "the situation to find lessons for" } },
        ["required"] = new JsonArray("query"),
    };
    public async Task<string> InvokeAsync(JsonObject args, CancellationToken ct = default)
    {
        var q = args["query"]?.GetValue<string>() ?? "";
        var hits = await _store.RetrieveAsync(_agent, new AgentFeatures(_sector, Array.Empty<string>(), q), 3, ct);
        return hits.Count == 0 ? "no relevant lessons" : string.Join("\n", hits.Select(l => "• " + l.Warning));
    }
}

/// <summary>A deterministic environment tool — margin of safety = (intrinsic − price) / intrinsic (read-only).</summary>
public sealed class ComputeMosTool : ITool
{
    public string Name => "margin_of_safety";
    public string Description => "Compute the margin of safety = (intrinsic - price) / intrinsic. Returns a fraction.";
    public bool ReadOnly => true;
    public JsonObject Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { ["intrinsic"] = new JsonObject { ["type"] = "number" }, ["price"] = new JsonObject { ["type"] = "number" } },
        ["required"] = new JsonArray("intrinsic", "price"),
    };
    public Task<string> InvokeAsync(JsonObject args, CancellationToken ct = default)
    {
        var i = args["intrinsic"]?.GetValue<double>() ?? 0; var p = args["price"]?.GetValue<double>() ?? 0;
        return Task.FromResult(i == 0 ? "0" : ((i - p) / i).ToString("0.###"));
    }
}

/// <summary>MCP seam (Claude Code Ch 15) — connects an MCP server and wraps its tools as <see cref="ITool"/>.
/// Transport/OAuth is the documented fast-follow; MCP tools default to gated (ReadOnly=false) until the
/// operator marks them safe.</summary>
public sealed class McpToolSource : IToolSource
{
    public McpToolSource(string endpoint) => Endpoint = endpoint;
    public string Endpoint { get; }
    public IReadOnlyList<ITool> Tools => Array.Empty<ITool>(); // TODO: connect, list_tools, wrap each as ITool (gated)
}

/// <summary>
/// The tool-use loop (OpenAI function-calling, adopted from Claude Code Ch 6-7). The model may call the
/// given tools; read-only tools run freely, others must pass <paramref name="permit"/> (the safety
/// partition). Loops until the model returns a final answer or the step budget is hit. Reuses AGENT_LLM_*.
/// </summary>
public static class ToolLoop
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private static string? Env(string k) => Environment.GetEnvironmentVariable(k);
    public static bool Enabled => !string.IsNullOrWhiteSpace(Env("AGENT_LLM_BASE_URL"));

    public static async Task<string> RunAsync(
        string system, string user, IReadOnlyList<ITool> tools,
        Func<string, JsonObject, bool>? permit = null, Action<string, string>? onCall = null,
        int maxSteps = 5, CancellationToken ct = default)
    {
        if (!Enabled) throw new InvalidOperationException("AGENT_LLM_* not set — the tool loop needs a live model.");
        var history = new List<JsonNode> { new JsonObject { ["role"] = "system", ["content"] = system }, new JsonObject { ["role"] = "user", ["content"] = user } };
        var toolDefs = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.Parameters.DeepClone() },
        }).ToArray());

        for (var step = 0; step < maxSteps; step++)
        {
            var msg = await Chat(history, toolDefs, ct);
            if (msg["tool_calls"] is not JsonArray calls || calls.Count == 0)
                return msg["content"]?.GetValue<string>() ?? "";

            history.Add(msg);
            foreach (var tc in calls)
            {
                var fn = tc!["function"]!;
                var name = fn["name"]!.GetValue<string>();
                var id = tc["id"]?.GetValue<string>() ?? name;
                var args = JsonNode.Parse(fn["arguments"]?.GetValue<string>() ?? "{}") as JsonObject ?? new JsonObject();
                var tool = tools.FirstOrDefault(t => t.Name == name);
                string result =
                    tool is null ? "error: unknown tool"
                    : !tool.ReadOnly && permit is not null && !permit(name, args) ? "denied: this tool requires human approval"
                    : await tool.InvokeAsync(args, ct);
                onCall?.Invoke(name, result);
                history.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = result });
            }
        }
        return "(tool-step budget reached without a final answer)";
    }

    private static async Task<JsonObject> Chat(List<JsonNode> history, JsonArray toolDefs, CancellationToken ct)
    {
        var url = Env("AGENT_LLM_BASE_URL")!.TrimEnd('/') + "/chat/completions";
        var ver = Env("AGENT_LLM_API_VERSION");
        if (!string.IsNullOrWhiteSpace(ver)) url += (url.Contains('?') ? "&" : "?") + "api-version=" + ver;
        var payload = new JsonObject
        {
            ["model"] = Env("AGENT_LLM_MODEL") is { Length: > 0 } m ? m : "gpt-4o-mini",
            ["temperature"] = 0,
            ["messages"] = new JsonArray(history.Select(h => h.DeepClone()).ToArray()),
            ["tools"] = toolDefs.DeepClone(),
            ["tool_choice"] = "auto",
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json") };
        var key = Env("AGENT_LLM_API_KEY");
        if (!string.IsNullOrWhiteSpace(key))
        {
            if ((Env("AGENT_LLM_AUTH") ?? "bearer").ToLowerInvariant() == "api-key") req.Headers.Add("api-key", key);
            else req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
        using var resp = await Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
        return ((node?["choices"]?[0]?["message"] as JsonObject)?.DeepClone() as JsonObject) ?? new JsonObject { ["content"] = "" };
    }
}
