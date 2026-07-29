using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace AIAssistant.Harness;

/// <summary>
/// Memory v2, step 3 — the LLM recall side-query (the Claude Code steal): from a cheap vector shortlist,
/// ask a small model which lessons ACTUALLY apply to the situation (applicability, not mere similarity).
/// Self-contained (reuses <c>AGENT_LLM_*</c>); returns <c>null</c> when no model is configured or the call
/// fails, so the caller degrades to plain vector ordering — recall never breaks retrieval.
/// </summary>
public static class Recall
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static string? Env(string k) => Environment.GetEnvironmentVariable(k);
    public static bool Enabled => !string.IsNullOrWhiteSpace(Env("AGENT_LLM_BASE_URL"));

    /// <summary>Ordered ids that apply (most-relevant first), or null if recall is unavailable.</summary>
    public static async Task<List<string>?> ApplicableAsync(
        string situation, IReadOnlyList<(string Id, string Cond, string Summary)> candidates, int k, CancellationToken ct = default)
    {
        if (!Enabled || candidates.Count == 0) return null;
        try
        {
            const string sys =
                "You decide which past lessons APPLY to the current situation — applicability, not mere word similarity. " +
                "Return ONLY JSON: {\"ids\":[...]} listing the applicable lesson ids, most-relevant first, at most the requested count. " +
                "If none apply, return an empty list.";
            var listing = string.Join("\n", candidates.Select(c => $"- {c.Id}: WHEN {c.Cond}. {c.Summary}"));
            var user = $"CURRENT SITUATION:\n{situation}\n\nCANDIDATE LESSONS:\n{listing}\n\nReturn at most {k} ids that genuinely apply.";
            var content = await Chat(sys, user, ct);
            var ids = JsonNode.Parse(content)?["ids"]?.AsArray();
            return ids?.Select(n => n?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToList() ?? new List<string>();
        }
        catch { return null; }
    }

    private static async Task<string> Chat(string sys, string user, CancellationToken ct)
    {
        var url = Env("AGENT_LLM_BASE_URL")!.TrimEnd('/') + "/chat/completions";
        var ver = Env("AGENT_LLM_API_VERSION");
        if (!string.IsNullOrWhiteSpace(ver)) url += (url.Contains('?') ? "&" : "?") + "api-version=" + ver;
        var model = Env("AGENT_LLM_MODEL") is { Length: > 0 } m ? m : "gpt-4o-mini";
        var payload = new JsonObject
        {
            ["model"] = model, ["temperature"] = 0, ["max_tokens"] = 200,
            ["response_format"] = new JsonObject { ["type"] = "json_object" },
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = sys },
                new JsonObject { ["role"] = "user", ["content"] = user }),
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
        return JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()
               ?? throw new InvalidOperationException("no content");
    }
}
