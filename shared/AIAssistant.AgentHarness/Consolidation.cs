namespace AIAssistant.Harness;

// ─────────────────────────────────────────────────────────────────────────────
// Memory consolidation — the answer to "what happens at scale?" Dedup merges NEAR-
// IDENTICAL lessons (cosine ≥ 0.92). Consolidation is the tier above it: a CLUSTER of
// RELATED-but-distinct lessons (same theme, different wording) is distilled into ONE
// higher-level meta-lesson. Memory stops growing linearly with experience — it
// summarizes itself, so retrieval stays sharp and the injected context stays small no
// matter how much the agent has learned. This is the fast loop's own compaction.
//
// The summarizer reuses AGENT_LLM_* and is inert offline (a deterministic digest), so
// consolidation always works — it just distils better with a model.
// ─────────────────────────────────────────────────────────────────────────────
public static class Consolidation
{
    /// <summary>Distil several related warnings into ONE compact meta-rule. LLM when configured; a terse
    /// deterministic union otherwise. Always returns something short enough to stay injectable.</summary>
    public static async Task<string> SummarizeAsync(IReadOnlyList<string> warnings, CancellationToken ct = default)
    {
        var distinct = warnings.Select(w => w.Trim()).Where(w => w.Length > 0).Distinct().ToList();
        if (distinct.Count == 0) return "";
        if (distinct.Count == 1) return distinct[0];

        if (ToolLoop.Enabled)
        {
            try
            {
                var listing = string.Join("\n", distinct.Select(w => "• " + w));
                var meta = await ToolLoop.CompleteAsync(
                    "You compress several related agent guidance rules into ONE broader rule that implies them all. " +
                    "Reply with a single imperative sentence, max 25 words. No preamble, no list.",
                    listing, 0, ct);
                meta = meta.Trim();
                if (meta.Length is > 0 and <= 400) return meta;
            }
            catch { /* fall through to deterministic */ }
        }
        return Digest(distinct);
    }

    // deterministic fallback: a compact numbered union, clipped so the meta stays smaller than its parts.
    private static string Digest(IReadOnlyList<string> ws)
    {
        var joined = string.Join(" ", ws.Take(6).Select((w, i) => $"({i + 1}) {w.TrimEnd('.')}."));
        return joined.Length <= 380 ? joined : joined[..380] + "…";
    }
}
