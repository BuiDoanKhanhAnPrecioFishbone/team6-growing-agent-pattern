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

/// <summary>A web-search backend. Keyless by default (Wikipedia); a keyed provider can be swapped in via
/// <see cref="WebSearch.FromEnvironment"/> without touching the tool that uses it.</summary>
public interface IWebSearch { Task<string> SearchAsync(string query, CancellationToken ct = default); }

/// <summary>Keyless web search over Wikipedia's public API — grounds a cheap model in real facts with no
/// secret to configure. Returns the top hit's clean summary plus a couple of snippets (HTML stripped),
/// trimmed so it fits a prompt.</summary>
public sealed class WikipediaSearch : IWebSearch
{
    private static readonly HttpClient Http = MakeClient();
    private static HttpClient MakeClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        h.DefaultRequestHeaders.Add("User-Agent", "GrowingAgentHarness/0.1 (teaching demo)"); // Wikipedia API etiquette
        return h;
    }

    public async Task<string> SearchAsync(string query, CancellationToken ct = default)
    {
        var searchUrl = "https://en.wikipedia.org/w/api.php?action=query&list=search&format=json&srlimit=3&srsearch="
                        + Uri.EscapeDataString(query);
        var hits = JsonNode.Parse(await Http.GetStringAsync(searchUrl, ct))?["query"]?["search"] as JsonArray;
        if (hits is null || hits.Count == 0) return "no results";

        var top = hits[0]!["title"]!.GetValue<string>();
        string summary;
        try
        {
            var sumUrl = "https://en.wikipedia.org/api/rest_v1/page/summary/" + Uri.EscapeDataString(top.Replace(' ', '_'));
            summary = JsonNode.Parse(await Http.GetStringAsync(sumUrl, ct))?["extract"]?.GetValue<string>() ?? "";
        }
        catch { summary = StripHtml(hits[0]!["snippet"]?.GetValue<string>() ?? ""); }

        var others = hits.Skip(1).Select(h => "• " + h!["title"]!.GetValue<string>() + ": " + StripHtml(h["snippet"]?.GetValue<string>() ?? ""));
        var body = $"{top}: {summary}\n{string.Join("\n", others)}".Trim();
        return body.Length > 700 ? body[..700] + "…" : body;
    }

    private static string StripHtml(string s) => System.Text.RegularExpressions.Regex.Replace(s, "<.*?>", "");
}

/// <summary>Picks a web-search backend from the environment — a keyed provider if one is configured, else
/// the keyless Wikipedia backend. The single place to add Brave/Bing/Tavily later; the tool never changes.</summary>
public static class WebSearch
{
    public static IWebSearch FromEnvironment() =>
        // e.g. if (Environment.GetEnvironmentVariable("BRAVE_API_KEY") is { Length: > 0 } k) return new BraveSearch(k);
        new WikipediaSearch();
}

/// <summary>Lets the agent look facts up instead of guessing — the antidote to a cheap model's #1 failure,
/// hallucination. Read-only, so it runs freely in the tool loop. Keyless by default (Wikipedia).</summary>
public sealed class WebSearchTool : ITool
{
    private readonly IWebSearch _search;
    public WebSearchTool(IWebSearch? search = null) => _search = search ?? WebSearch.FromEnvironment();
    public string Name => "web_search";
    public string Description => "Search the web for current, factual information before you answer — names, dates, figures, definitions you are not certain about. Prefer this over guessing.";
    public bool ReadOnly => true;
    public JsonObject Parameters => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { ["query"] = new JsonObject { ["type"] = "string", ["description"] = "what to look up" } },
        ["required"] = new JsonArray("query"),
    };
    public async Task<string> InvokeAsync(JsonObject args, CancellationToken ct = default)
    {
        var q = args["query"]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(q)) return "error: empty query";
        try { return await _search.SearchAsync(q, ct); }
        catch (Exception e) { return "search unavailable: " + e.Message; }
    }
}

/// <summary>The default self-verify critic (amplifier lever): a cheap LLM reviewer that flags concrete
/// problems in a draft the deterministic reward already passed — the soft errors a reward can't encode.
/// Inert when no model is configured, so offline runs stay fully deterministic.</summary>
public sealed class LlmCritic : ICritic
{
    private const string Sys =
        "You are a meticulous reviewer. Given a TASK INPUT and a CANDIDATE answer, list concrete, specific " +
        "problems with the candidate: factual errors, invented or unsupported claims, missing required parts, " +
        "internal contradictions. Use terse bullet points. If the candidate is fully correct and complete, " +
        "reply with exactly: OK";

    public async Task<string?> CritiqueAsync(AgentContext ctx, string draft, Reward reward, CancellationToken ct)
    {
        if (!ToolLoop.Enabled) return null; // no live model ⇒ inert
        string verdict;
        try { verdict = (await ToolLoop.CompleteAsync(Sys, $"TASK INPUT:\n{ctx.Input.ToJsonString()}\n\nCANDIDATE:\n{draft}", 0, ct)).Trim(); }
        catch { return null; }              // a critic failure must never break the loop
        return verdict.Length == 0 || verdict.TrimStart('#', '*', '-', ' ', '`').StartsWith("OK", StringComparison.OrdinalIgnoreCase)
            ? null : verdict;
    }
}

// McpToolSource + the MCP client live in Mcp.cs — a real stdio JSON-RPC client that connects to an MCP
// server, lists its tools, and wraps each as a gated ITool.

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
        var budget = ContextBudget.FromEnvironment(); // long-session context management (off unless AGENT_CONTEXT_TOKENS set)
        var history = new List<JsonNode> { new JsonObject { ["role"] = "system", ["content"] = system }, new JsonObject { ["role"] = "user", ["content"] = user } };
        var toolDefs = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.Parameters.DeepClone() },
        }).ToArray());

        for (var step = 0; step < maxSteps; step++)
        {
            Context.CompactToolHistory(history, budget); // keep a long tool session within its token budget
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

    /// <summary>A single plain-text completion with no tools — the "bare model" baseline, and the building
    /// block for self-verify / escalation. Reuses AGENT_LLM_*.</summary>
    public static Task<string> CompleteAsync(string system, string user, double temperature = 0, CancellationToken ct = default)
        => CompleteMessagesAsync(new[] { new ChatTurn("system", system), new ChatTurn("user", user) }, temperature, ct);

    /// <summary>A plain-text completion over a full message list (no tools) — lets a caller send a managed
    /// conversation (e.g. one compacted by <see cref="Context"/>). Reuses AGENT_LLM_*.</summary>
    public static async Task<string> CompleteMessagesAsync(IReadOnlyList<ChatTurn> messages, double temperature = 0, CancellationToken ct = default)
    {
        if (!Enabled) throw new InvalidOperationException("AGENT_LLM_* not set — a live model is required.");
        var url = Env("AGENT_LLM_BASE_URL")!.TrimEnd('/') + "/chat/completions";
        var ver = Env("AGENT_LLM_API_VERSION");
        if (!string.IsNullOrWhiteSpace(ver)) url += (url.Contains('?') ? "&" : "?") + "api-version=" + ver;
        var payload = new JsonObject
        {
            ["model"] = Env("AGENT_LLM_MODEL") is { Length: > 0 } m ? m : "gpt-4o-mini",
            ["temperature"] = temperature,
            ["messages"] = new JsonArray(messages.Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content }).ToArray()),
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
        return node?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
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
