namespace AIAssistant.Harness;

// ─────────────────────────────────────────────────────────────────────────────
// ADOPTION ERGONOMICS — the whole pattern's promise is "you write a reward, not a
// framework." That's only true if writing the reward is trivial. These two helpers
// make it so: Checks turns a list of named pass/fail (or weighted) assertions into a
// full Reward — score, failed-triggers, and critique derived for you — and Quickstart
// wires the harness with sane defaults so a first run is a single call.
//
// The named check labels ARE the FailedTriggers, which is exactly what the harness
// feeds to IAgent.LessonFor — so a failed check names the lesson it teaches. One list
// drives scoring, learning, and the critique the revise step reads.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>One named assertion in a reward rubric: a human-readable label and whether the draft met it.</summary>
public readonly record struct Check(string Label, bool Ok, double Weight = 1.0);

/// <summary>
/// Build a <see cref="Reward"/> from named checks. <see cref="All"/> weights every check equally;
/// <see cref="Weighted"/> honours per-check weights. In both, Pass ⇔ every check passed, Score is the
/// (weighted) fraction met, FailedTriggers are the failed labels, and Critique lists what to fix — so a
/// domain reward is a few lines, and its failures automatically drive learning.
/// </summary>
public static class Checks
{
    public static Reward All(params Check[] checks) => Weighted(checks);

    /// <summary>Convenience: pass tuples instead of Check structs — <c>Checks.Of(("cites a source", hasCite), …)</c>.</summary>
    public static Reward Of(params (string label, bool ok)[] checks) =>
        Weighted(checks.Select(c => new Check(c.label, c.ok)).ToArray());

    public static Reward Weighted(params Check[] checks)
    {
        if (checks.Length == 0) return Reward.Scored(true, 1.0, "No checks defined.");
        var breakdown = new Dictionary<string, double>();
        var failed = new HashSet<string>();
        double got = 0, total = 0;
        foreach (var c in checks)
        {
            var w = c.Weight <= 0 ? 1.0 : c.Weight;
            total += w;
            breakdown[c.Label] = c.Ok ? 1.0 : 0.0;
            if (c.Ok) got += w; else failed.Add(c.Label);
        }
        var score = total <= 0 ? 1.0 : got / total;
        var pass = failed.Count == 0;
        var critique = pass ? "All checks passed."
            : "Fix: " + string.Join("; ", failed);
        return new Reward(pass, Math.Clamp(score, 0, 1), breakdown, failed, critique);
    }
}

/// <summary>
/// One-call harness wiring for adopters. <c>GrowingAgent.Quickstart()</c> gives a working loop with a JSON
/// memory and env-driven options; pass a critic/escalation to opt into the amplifier levers. Everything the
/// dev must supply is their <see cref="IAgent"/> (really just the reward). This is the "20 lines and it grows"
/// entry point the docs and (later) the skill point at.
/// </summary>
public static class GrowingAgent
{
    /// <summary>Wire a harness with a JSON lesson store at <paramref name="memoryPath"/> and default options.</summary>
    public static AgentHarness Quickstart(string memoryPath = "memory.json", ICritic? critic = null, EscalateDraft? escalate = null)
        => new(new JsonLessonStore(memoryPath), critic: critic, escalate: escalate);

    /// <summary>Same, but with the semantic (embedding) store — recall by situation once lessons accumulate.</summary>
    public static AgentHarness QuickstartSemantic(string memoryPath = "memory.json", ICritic? critic = null, EscalateDraft? escalate = null)
        => new(new SemanticLessonStore(memoryPath), critic: critic, escalate: escalate);

    /// <summary>The default options (env-overridable): 3 iters, best-of-1, top-3 recall.</summary>
    public static HarnessOptions Options() => HarnessOptions.FromEnvironment();
}
