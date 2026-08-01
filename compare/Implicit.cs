using System.Text.Json.Nodes;
using AIAssistant.Harness;

namespace Compare;

// "Learn from use" — the growing agent with NO teaching UI. The user just does what users do: edits the AI's
// answer, hits thumbs-down, or regenerates. The implicit-signal adapter turns that ordinary action into a
// Reward and mints a Provisional lesson, which the very next generation applies. Nobody taught the agent a
// rule; it learned one from the exhaust of normal usage. (Contrast the Teach tab, where a human states a
// Verified rule directly — here lessons are auto-mined and start Provisional: tried, not yet believed.)
public static class ImplicitRun
{
    private static readonly SemanticLessonStore Store = new(Path.Combine(Path.GetTempPath(), "compare-implicit.json"));
    private const string Agent = "implicit-advisor", Sector = "advisory";
    private const string Task =
        "Write a short investment note (2–3 sentences) recommending whether to buy shares of \"VNM\", a consumer-staples company.";

    public static async Task<object> RunAsync(JsonObject body, CancellationToken ct)
    {
        if (body["reset"]?.GetValue<bool>() ?? false) Store.Clear();

        // If the user acted on the last answer, LEARN from it first — this is the whole point.
        object? learned = null;
        if (body["signal"] is JsonObject sig)
        {
            var kind = Enum.TryParse<SignalKind>(sig["kind"]?.GetValue<string>(), ignoreCase: true, out var k) ? k : SignalKind.Edit;
            var signal = new ImplicitSignal(
                kind, Task,
                Output: sig["output"]?.GetValue<string>() ?? "",
                Correction: sig["correction"]?.GetValue<string>(),
                Features: new AgentFeatures(Sector, Array.Empty<string>(), "writing an investment note"));

            var reward = ImplicitReward.ToReward(signal);
            var lesson = await ImplicitLearner.LearnAsync(signal, Store, Agent, ImplicitLearner.DefaultRuleDeriver(), ct: ct);
            learned = new
            {
                kind = kind.ToString(),
                reward = new { pass = reward.Pass, score = Math.Round(reward.Score, 2), critique = reward.Critique },
                lesson = lesson is null ? null : (object)new { warning = lesson.Warning, trust = lesson.Trust.ToString(), from = lesson.LearnedFrom },
            };
        }

        // Generate WITH everything the store now holds — Provisional lessons included (they're tried, not
        // trusted). This is faithful to production: an auto-mined lesson takes effect immediately, then earns
        // (or loses) trust via hit-rate over subsequent runs.
        var lessons = (await Store.AllAsync()).Where(l => l.Agent == Agent && l.Trust != Trust.Quarantined).ToList();

        string output;
        if (!ToolLoop.Enabled) output = "(set AGENT_LLM_* to run live)";
        else
        {
            var sys = "You are an equity analyst writing a brief, professional investment note.";
            if (lessons.Count > 0)
                sys += "\n\nWhat this agent learned from how users edited past answers — follow every rule:"
                     + string.Concat(lessons.Select(l => "\n• " + l.Warning));
            output = await ToolLoop.CompleteAsync(sys, Task, 0.2, ct);
        }

        return new
        {
            task = Task, output, learned,
            lessons = lessons.Select(l => new { warning = l.Warning, trust = l.Trust.ToString() }).ToList(),
        };
    }
}
