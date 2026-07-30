using System.Text.Json.Nodes;

namespace AIAssistant.Harness;

/// <summary>
/// Working-context budget — the short-term counterpart to the lesson memory. The lesson store is the agent's
/// LONG-term memory (curated across runs); this governs the SHORT-term context of a single long session: how
/// many tokens of conversation / tool output the model carries at once. Off by default (<c>MaxTokens=0</c>).
/// </summary>
public sealed record ContextBudget(int MaxTokens, int KeepRecent = 4)
{
    public bool Enabled => MaxTokens > 0;
    public static ContextBudget FromEnvironment() =>
        new(EnvInt("AGENT_CONTEXT_TOKENS", 0), Math.Max(1, EnvInt("AGENT_CONTEXT_KEEP_RECENT", 4)));
    private static int EnvInt(string k, int d) => int.TryParse(Environment.GetEnvironmentVariable(k), out var v) ? v : d;
}

/// <summary>One conversation turn (role + text) for the generic compactor.</summary>
public sealed record ChatTurn(string Role, string Content);

/// <summary>
/// Context management for long sessions — mirrors Claude Code's compaction: recent detail stays sharp, older
/// detail becomes gist, so the context window (and cost) stay bounded no matter how long the session runs.
/// Two entry points: <see cref="FitAsync"/> for a plain conversation, <see cref="CompactToolHistory"/> for a
/// live tool-loop message list (structure-preserving, so tool-call pairing stays valid).
/// </summary>
public static class Context
{
    /// <summary>Rough GPT-style token estimate (~4 chars/token) — enough for budgeting without a tokenizer.</summary>
    public static int EstimateTokens(string? s) => string.IsNullOrEmpty(s) ? 0 : (s!.Length + 3) / 4;
    public static int EstimateTokens(IEnumerable<ChatTurn> turns) => turns.Sum(t => EstimateTokens(t.Content) + 4);

    /// <summary>A default LLM summarizer for <see cref="FitAsync"/>, or null when no model is configured.</summary>
    public static Func<string, CancellationToken, Task<string>>? LlmSummarizer() =>
        ToolLoop.Enabled
            ? (text, ct) => ToolLoop.CompleteAsync(
                "Summarize the conversation below into terse notes that preserve facts, decisions, numbers and open threads. No preamble.",
                text, 0, ct)
            : null;

    /// <summary>
    /// Compact a conversation to fit <paramref name="budget"/>: keep the system message and the last
    /// <c>KeepRecent</c> turns verbatim; fold everything older into ONE summary turn (via
    /// <paramref name="summarize"/> if given, else a deterministic digest). Returns a new list; a
    /// conversation already under budget is returned unchanged.
    /// </summary>
    public static async Task<List<ChatTurn>> FitAsync(
        IReadOnlyList<ChatTurn> turns, ContextBudget budget,
        Func<string, CancellationToken, Task<string>>? summarize = null, CancellationToken ct = default)
    {
        var list = turns.ToList();
        if (!budget.Enabled || EstimateTokens(list) <= budget.MaxTokens) return list;

        var sys = list.Count > 0 && list[0].Role == "system" ? list[0] : null;
        var start = sys is null ? 0 : 1;
        var tailStart = Math.Max(start, list.Count - budget.KeepRecent);
        var older = list.Skip(start).Take(tailStart - start).ToList();
        var tail = list.Skip(tailStart).ToList();
        if (older.Count == 0) return list; // only system + recent remain — nothing left to compact

        var blob = string.Join("\n", older.Select(t => $"{t.Role}: {t.Content}"));
        string summary;
        if (summarize is not null) { try { summary = await summarize(blob, ct); } catch { summary = Digest(older); } }
        else summary = Digest(older);

        var result = new List<ChatTurn>();
        if (sys is not null) result.Add(sys);
        result.Add(new ChatTurn("system", "Summary of the earlier conversation (compacted to save context):\n" + summary));
        result.AddRange(tail);
        return result;
    }

    /// <summary>
    /// Bound a live tool-loop history in place: while it exceeds <paramref name="budget"/>, trim the OLDEST
    /// bulky tool-result message to a stub. Only <c>content</c> strings change — roles and
    /// <c>tool_call_id</c>s are untouched, so the assistant/tool pairing the API requires stays intact.
    /// </summary>
    public static void CompactToolHistory(List<JsonNode> history, ContextBudget budget)
    {
        if (!budget.Enabled) return;
        int Tok(JsonNode? m) => EstimateTokens((m?["content"] as JsonValue)?.GetValue<string>());
        int Total() => history.Sum(Tok);

        var recentFrom = history.Count - budget.KeepRecent;
        for (var i = 0; i < history.Count && Total() > budget.MaxTokens; i++)
        {
            if (i >= recentFrom) break; // never trim the most recent turns
            var m = history[i];
            if ((m?["role"] as JsonValue)?.GetValue<string>() != "tool") continue;
            var c = (m!["content"] as JsonValue)?.GetValue<string>() ?? "";
            if (c.Length > 80) m["content"] = "[older tool result trimmed to save context]";
        }
    }

    // deterministic fallback when no summarizer/model — keep the gist: role + a clipped line per older turn.
    private static string Digest(IReadOnlyList<ChatTurn> older)
    {
        var kept = older.TakeLast(12).Select(t => $"- {t.Role}: {Clip(t.Content, 120)}");
        var omitted = older.Count > 12 ? $"(+{older.Count - 12} earlier turns omitted)\n" : "";
        return omitted + string.Join("\n", kept);
    }
    private static string Clip(string s, int n) { s = s.Replace('\n', ' ').Trim(); return s.Length <= n ? s : s[..n] + "…"; }
}
