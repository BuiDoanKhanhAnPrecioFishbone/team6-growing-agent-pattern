using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using AIAssistant.Harness;

namespace AIAssistant.AgentHost;

public sealed record ChatMessage(string Role, string Content);

/// <summary>
/// Live-model config from the environment (<c>AGENT_LLM_*</c>). When no base URL is set, agents run their
/// deterministic mock — the pipeline works fully offline. Set the vars to route every agent through an
/// Azure AI Foundry deployment. <c>AGENT_LLM_AUTH=api-key</c> + <c>AGENT_LLM_API_VERSION</c> switch to
/// classic Azure OpenAI; the default (bearer, no version) matches the Foundry <c>/openai/v1</c> route.
/// </summary>
public sealed record ChatOptions(string? BaseUrl, string? ApiKey, string ModelName, double Temperature, string? ApiVersion, string AuthStyle, string? StrongModelName = null)
{
    public bool Enabled => !string.IsNullOrWhiteSpace(BaseUrl);

    public static ChatOptions FromEnvironment()
    {
        static string? Get(string k) => Environment.GetEnvironmentVariable(k);
        static double D(string k, double d) => double.TryParse(Get(k), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : d;
        return new ChatOptions(
            BaseUrl: Get("AGENT_LLM_BASE_URL"),
            ApiKey: Get("AGENT_LLM_API_KEY"),
            ModelName: Get("AGENT_LLM_MODEL") is { Length: > 0 } m ? m : "gpt-4o-mini",
            Temperature: D("AGENT_LLM_TEMPERATURE", 0.3),
            ApiVersion: Get("AGENT_LLM_API_VERSION"),
            AuthStyle: (Get("AGENT_LLM_AUTH") ?? "bearer").ToLowerInvariant(),
            // Escalation tier: the loop calls this bigger deployment only on hard cases (below threshold).
            StrongModelName: Get("AGENT_LLM_MODEL_STRONG"));
    }
}

/// <summary>The ambient model every agent generates through. Configured once at startup; when disabled,
/// <see cref="Generate"/> returns the agent's deterministic draft unchanged (offline mock).</summary>
public static class Model
{
    private static ChatOptions _opt = new(null, null, "gpt-4o-mini", 0.3, null, "bearer");
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };
    private static readonly AsyncLocal<bool> _useStrong = new(); // scoped: escalation attempts run on the strong tier

    public static bool Enabled => _opt.Enabled;
    public static string Name => _opt.Enabled ? _opt.ModelName : "mock (offline)";

    /// <summary>Whether an escalation (strong) deployment is configured — gates the cascade lever.</summary>
    public static bool StrongConfigured => !string.IsNullOrWhiteSpace(_opt.StrongModelName);
    public static string? StrongName => _opt.StrongModelName;
    private static string CurrentModel() => _useStrong.Value && StrongConfigured ? _opt.StrongModelName! : _opt.ModelName;

    /// <summary>Run <paramref name="f"/> with the strong model selected — the harness's escalation hook.</summary>
    public static async Task<T> WithStrongModel<T>(Func<Task<T>> f)
    {
        _useStrong.Value = true;
        try { return await f(); } finally { _useStrong.Value = false; }
    }

    /// <summary>Read AGENT_LLM_* once at process start.</summary>
    public static void Configure() => _opt = ChatOptions.FromEnvironment();

    /// <summary>
    /// In mock mode: returns <paramref name="templateBlock"/> (the agent's deterministic draft). In live
    /// mode: asks the Foundry model to produce this agent's block JSON, grounded in the candidate + sources
    /// and obeying the injected lessons; the agent's reward still gates it. Any failure degrades to the mock.
    /// </summary>
    public static async Task<string> Generate(string templateBlock, AgentContext ctx, IReadOnlyList<Lesson> lessons, string? critique, string agentId, CancellationToken ct = default)
    {
        if (!_opt.Enabled) return templateBlock;
        try
        {
            var sys = $"You are the {agentId} step of a value-investing pipeline. Return ONLY a JSON object with the SAME KEYS as the TEMPLATE, grounded strictly in the CANDIDATE and SOURCES. Never invent a source, page or number that is not present. Obey every LESSON. Draft/judgment fields stay drafts (humanConfirmed=false).";
            var user = new StringBuilder()
                .AppendLine("TEMPLATE (match this JSON shape exactly):").AppendLine(templateBlock).AppendLine()
                .AppendLine("CANDIDATE:").AppendLine(ctx.Input.ToJsonString()).AppendLine()
                .AppendLine("SOURCES (cite only these):");
            foreach (var s in ctx.AllowedSources) user.AppendLine("  - " + s);
            if (lessons.Count > 0) { user.AppendLine().AppendLine("LESSONS (learned from earlier mistakes — obey them):"); foreach (var l in lessons) user.AppendLine("  • " + l.Warning); }
            if (critique is not null) { user.AppendLine().AppendLine("Your previous draft failed the reward. Fix ALL of this and return the FULL corrected JSON:").AppendLine(critique); }

            var content = await Complete(new[] { new ChatMessage("system", sys), new ChatMessage("user", user.ToString()) }, ct);
            _ = JsonNode.Parse(content); // sanity: must be JSON, else fall through to the mock
            return content;
        }
        catch
        {
            return templateBlock; // never break the flow because the LLM is unreachable
        }
    }

    private static async Task<string> Complete(IReadOnlyList<ChatMessage> messages, CancellationToken ct)
    {
        var url = _opt.BaseUrl!.TrimEnd('/') + "/chat/completions";
        if (!string.IsNullOrWhiteSpace(_opt.ApiVersion)) url += (url.Contains('?') ? "&" : "?") + "api-version=" + _opt.ApiVersion;

        var payload = new JsonObject
        {
            ["model"] = CurrentModel(),
            ["temperature"] = _opt.Temperature,
            ["max_tokens"] = 1200,
            ["response_format"] = new JsonObject { ["type"] = "json_object" },
            ["messages"] = new JsonArray(messages.Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content }).ToArray()),
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json") };
        if (!string.IsNullOrWhiteSpace(_opt.ApiKey))
        {
            if (_opt.AuthStyle == "api-key") req.Headers.Add("api-key", _opt.ApiKey);
            else req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opt.ApiKey);
        }
        using var resp = await Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct);
        return JsonNode.Parse(body)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()
               ?? throw new InvalidOperationException("no content");
    }
}
