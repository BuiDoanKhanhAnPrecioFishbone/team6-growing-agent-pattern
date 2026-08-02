namespace AIAssistant.Harness;

// ─────────────────────────────────────────────────────────────────────────────
// Stored IMPORTANCE (poignancy) — the most-cited retrieval signal in the field
// (Generative Agents): score how much a lesson matters WHEN IT'S WRITTEN, keep it on
// the record, and fold it into ranking alongside relevance + recency + trust. A cheap
// model rates it live; offline a deterministic heuristic stands in, so it always works.
// ─────────────────────────────────────────────────────────────────────────────
public static class Importance
{
    /// <summary>Deterministic 0–1 fallback: grounding rules and domain facts matter more than incidental tips;
    /// a lesson that carries an explicit condition is a touch more valuable than an unscoped one.</summary>
    public static double Heuristic(Lesson l)
    {
        var b = l.Type switch
        {
            LessonType.GroundingRule => 0.72,
            LessonType.DomainFact    => 0.66,
            LessonType.Strategy      => 0.56,
            LessonType.ToolTip       => 0.50,
            _                        => 0.50,
        };
        if (!string.IsNullOrWhiteSpace(l.Condition)) b += 0.05;
        return Math.Clamp(b, 0, 1);
    }

    /// <summary>Score a lesson's importance. LLM (1–10 → 0–1) when a model is configured; heuristic otherwise.</summary>
    public static async Task<double> ScoreAsync(Lesson l, CancellationToken ct = default)
    {
        if (!ToolLoop.Enabled) return Heuristic(l);
        try
        {
            var r = await ToolLoop.CompleteAsync(
                "Rate how IMPORTANT this reusable rule is for an agent to remember, 1 (trivial) to 10 (critical). Reply with ONLY the number.",
                $"WHEN {l.Condition}: {l.Warning}", 0, ct);
            var digits = new string(r.Where(char.IsDigit).ToArray());
            if (digits.Length > 0 && int.TryParse(digits.Length > 2 ? digits[..2] : digits, out var v) && v is >= 1 and <= 10)
                return v / 10.0;
        }
        catch { /* fall through */ }
        return Heuristic(l);
    }
}
