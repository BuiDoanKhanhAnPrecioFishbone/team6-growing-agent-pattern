using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace AIAssistant.Harness;

/// <summary>
/// Memory-lifecycle, the contradiction check — the semantic complement to deterministic dedup. When a new
/// lesson is <em>related</em> to an existing verified one (similar situation) but might tell the agent to do
/// the OPPOSITE, a cheap model decides whether they genuinely conflict. Self-contained (reuses
/// <c>AGENT_LLM_*</c>); returns <c>null</c> when no model is configured or the call fails, so writes never
/// break — offline, the store relies on the deterministic same-trigger divergence check instead.
/// </summary>
public static class Conflict
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static string? Env(string k) => Environment.GetEnvironmentVariable(k);
    public static bool Enabled => !string.IsNullOrWhiteSpace(Env("AGENT_LLM_BASE_URL"));

    /// <summary>The id of an existing lesson the candidate CONTRADICTS (following both is impossible), or null.</summary>
    public static async Task<string?> ContradictsAsync(
        string candidate, IReadOnlyList<(string Id, string Text)> neighbors, CancellationToken ct = default)
    {
        if (!Enabled || neighbors.Count == 0) return null;
        try
        {
            const string sys =
                "You detect CONTRADICTIONS between guidance rules for an agent. Two rules conflict only when " +
                "following BOTH at once is impossible — not when they are merely different or about different things. " +
                "Return ONLY JSON: {\"conflictId\":\"<id>\"} with the id of the ONE existing rule the candidate " +
                "contradicts, or {\"conflictId\":\"\"} if none conflict.";
            var listing = string.Join("\n", neighbors.Select(n => $"- {n.Id}: {n.Text}"));
            var user = $"CANDIDATE RULE:\n{candidate}\n\nEXISTING RULES:\n{listing}";
            var content = await Chat(sys, user, ct);
            var id = JsonNode.Parse(content)?["conflictId"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(id) ? null : id;
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
            ["model"] = model, ["temperature"] = 0, ["max_tokens"] = 60,
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
