namespace AIAssistant.Harness;

// ─────────────────────────────────────────────────────────────────────────────
// CURRICULUM — turn the free-data exhaust into DIRECTED improvement (Voyager's automatic
// curriculum). Passive capture already learns from whatever users happen to do; this
// reads the signals and says what to DRILL NEXT so the agent improves on purpose:
//   A. weak skills      — the checks that fail most often lately
//   B. unproven lessons — Provisional rules that haven't earned trust yet (confirm or retire)
//   C. regressing rules — once-Verified lessons whose hit-rate has slipped (re-validate)
// Deterministic ranking; an optional model turns a target into a concrete practice task.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>One thing worth practicing next, and why.</summary>
public sealed record PracticeItem(string Focus, string Reason, double Priority, string Kind);

public static class Curriculum
{
    /// <summary>Rank what to practice next from the current memory + recent rewards.</summary>
    public static IReadOnlyList<PracticeItem> Propose(
        IReadOnlyList<Lesson> lessons, IEnumerable<Reward> recentRewards, int top = 5)
    {
        var items = new List<PracticeItem>();

        // A. the checks that fail most often — highest priority, scaled by frequency.
        foreach (var g in recentRewards.SelectMany(r => r.FailedTriggers).GroupBy(t => t).OrderByDescending(g => g.Count()))
            items.Add(new PracticeItem(g.Key, $"failed {g.Count()}× recently", 2.0 + g.Count(), "weak-skill"));

        foreach (var l in lessons.Where(l => string.IsNullOrEmpty(l.ValidTo)))
        {
            // B. unproven provisional lessons — drill their situation to confirm (or retire) them.
            if (l.Trust == Trust.Provisional)
            {
                var need = Math.Max(1, 2 - l.HelpedContexts.Count);
                items.Add(new PracticeItem(Cond(l), $"unproven — needs {need} more distinct corroboration(s)", 1.0 + 0.5 * need, "unproven-lesson"));
            }
            // C. a once-trusted lesson whose hit-rate has slipped — re-validate it.
            else if (l.Trust == Trust.Verified && l.TimesApplied >= 3 && l.HitRate < 0.5)
                items.Add(new PracticeItem(Cond(l), $"regressing (hit-rate {l.HitRate:P0}) — re-validate", 1.5, "regressing"));
        }

        return items.OrderByDescending(i => i.Priority).Take(top).ToList();
    }

    /// <summary>Optional: turn a practice target into a concrete drill task via a domain model (inert offline).</summary>
    public static async Task<string?> SuggestTaskAsync(PracticeItem item, CancellationToken ct = default)
    {
        if (!ToolLoop.Enabled) return null;
        try
        {
            var t = await ToolLoop.CompleteAsync(
                "You invent one short practice task that exercises a specific weak spot for an agent.",
                $"Weak spot: {item.Focus} ({item.Reason}). Write ONE practice task, one sentence. No preamble.", 0, ct);
            return string.IsNullOrWhiteSpace(t) ? null : t.Trim();
        }
        catch { return null; }
    }

    private static string Cond(Lesson l) => string.IsNullOrWhiteSpace(l.Condition) ? l.Warning : l.Condition;
}
