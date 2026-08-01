using System.Text.Json;

namespace AIAssistant.Harness;

/// <summary>One generation the loop produced, with the reward it earned. The raw material for training.</summary>
public sealed record Attempt(string Draft, double Score, bool Pass);

/// <summary>
/// The flywheel: every reward-labeled run the harness does is training data. We don't need ART/GRPO to ship
/// (the fast loop already grows the agent) — but every run exports, for free, a corpus a trainer can use
/// later. Three shapes from the same run:
///   • SFT        — the winning completion as the target (imitation)
///   • Preference — best vs a worse attempt (DPO / reward-model data)
///   • RL / GRPO  — every attempt tagged with its scalar reward (policy-gradient data)
/// This is what makes "we're ART-ready" concrete rather than aspirational.
/// </summary>
public static class TrainingExporter
{
    private static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web);

    /// <summary>SFT line — only when the run reached a passing answer. `{messages:[user, assistant]}`.</summary>
    public static string? Sft(string task, HarnessOutcome o) =>
        o.Best.Pass && !string.IsNullOrEmpty(o.BestDraft)
            ? JsonSerializer.Serialize(new
            {
                messages = new object[]
                {
                    new { role = "user", content = task },
                    new { role = "assistant", content = o.BestDraft },
                }
            }, Opts)
            : null;

    /// <summary>Preference line (DPO) — best vs the lowest-scored attempt, when there is a reward gap.</summary>
    public static string? Preference(string task, HarnessOutcome o)
    {
        var atts = o.Attempts;
        if (atts is null || atts.Count < 2) return null;
        var chosen = atts.MaxBy(a => a.Score)!;
        var rejected = atts.MinBy(a => a.Score)!;
        if (chosen.Score <= rejected.Score || chosen.Draft == rejected.Draft) return null;
        return JsonSerializer.Serialize(new { prompt = task, chosen = chosen.Draft, rejected = rejected.Draft }, Opts);
    }

    /// <summary>RL / GRPO lines — every attempt with its reward. `{prompt, completion, reward}`.</summary>
    public static IEnumerable<string> Rl(string task, HarnessOutcome o) =>
        (o.Attempts ?? Array.Empty<Attempt>())
            .Select(a => JsonSerializer.Serialize(new { prompt = task, completion = a.Draft, reward = a.Score }, Opts));
}
