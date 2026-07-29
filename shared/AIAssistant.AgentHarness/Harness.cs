using System.Text.Json.Nodes;

namespace AIAssistant.Harness;

// ─────────────────────────────────────────────────────────────────────────────
// THE AGENT HARNESS — the reusable substrate every S-agent plugs into.
//
//   pattern: "The Compounding Analyst" — an agent is a POLICY grown inside an
//   ENVIRONMENT, guided by ONE reward that gates it now and (later) trains it.
//
// The fast loop below is agent-agnostic: generate → evaluate → retrieve lesson →
// revise → pick best → write lesson. S2 (Moat) is the first agent to run in it;
// S3/S4 adopt the same contract. Nothing here knows what a "moat" is.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Scoping keys for episodic memory. <see cref="Situation"/> (v2) is a short text of the current
/// case that semantic retrieval recalls against; empty falls back to hit-rate ordering.</summary>
public sealed record AgentFeatures(string Sector, IReadOnlyList<string> Tags, string Situation = "");

/// <summary>
/// The reward — one function, triple duty: hard <see cref="Pass"/> gates, a graded <see cref="Score"/>,
/// an actionable <see cref="Critique"/>, and the <see cref="FailedTriggers"/> that drive both learning
/// and each lesson's hit-rate. This is the object a future ART/GRPO trainer optimizes verbatim.
/// </summary>
public sealed record Reward(
    bool Pass,
    double Score,
    IReadOnlyDictionary<string, double> Breakdown,
    IReadOnlySet<string> FailedTriggers,
    string Critique);

/// <summary>What a lesson is about — drives recall relevance and pruning policy (Memory v2).</summary>
public enum LessonType { GroundingRule, ToolTip, DomainFact, Strategy }

/// <summary>Whether a learned lesson may be injected. Provisional must earn hit-rate or a human gate before
/// it becomes Verified; Quarantined failed injection-validation and is never injected. (Memory v2.)</summary>
public enum Trust { Provisional, Verified, Quarantined }

/// <summary>
/// An episodic lesson: "in {Sector}, guard against {Trigger}." Carries the CONDITION it applies under
/// and its own <see cref="HitRate"/> so the memory self-corrects — a lesson that stops helping decays out
/// of retrieval instead of being blindly re-applied.
/// </summary>
public sealed class Lesson
{
    public string Id { get; set; } = "";          // "{agent}|{sector}|{trigger}" — the upsert key
    public string Agent { get; set; } = "";
    public string Sector { get; set; } = "";
    public string Trigger { get; set; } = "";
    public string Warning { get; set; } = "";     // the guidance injected into the next generation (phase-2 text)
    public string LearnedFrom { get; set; } = ""; // ticker/run that earned it — provenance
    public string Date { get; set; } = "";
    public int TimesApplied { get; set; }
    public int TimesHelped { get; set; }
    public double HitRate { get; set; }

    // ── Memory v2 ──
    public LessonType Type { get; set; } = LessonType.GroundingRule;
    public string Condition { get; set; } = "";                 // when it applies — short & embeddable
    public float[] Embedding { get; set; } = Array.Empty<float>(); // vector of (Condition + summary)
    public Trust Trust { get; set; } = Trust.Verified;          // D1-2 skeleton default; D5 = Provisional + promotion
    public string LastUsed { get; set; } = "";                  // for staleness
}

/// <summary>Everything an agent needs for one run. The Input is the candidate-file fragment (passthrough).</summary>
public sealed class AgentContext
{
    public required string Ticker { get; init; }
    public required AgentFeatures Features { get; init; }
    public required JsonObject Input { get; init; }
    /// <summary>The environment's grounding set: the sources the agent is allowed to cite. Empty ⇒ locatability only.</summary>
    public required IReadOnlyList<string> AllowedSources { get; init; }
}

/// <summary>The harness contract. Six agents, one shape.</summary>
public interface IAgent
{
    string Id { get; }

    /// <summary>Produce a draft. <paramref name="lessons"/> are injected up front; on revisions the
    /// prior draft and its <paramref name="critique"/> come back so the policy can fix, not restart.</summary>
    Task<string> GenerateAsync(
        AgentContext ctx, IReadOnlyList<Lesson> lessons,
        string? critique, string? priorDraft, int attempt, CancellationToken ct);

    /// <summary>The reward. Deterministic, unhackable, reproducible.</summary>
    Reward Evaluate(string draft, AgentContext ctx);

    /// <summary>Mint the lesson for a mistake the loop just fixed, so it isn't repeated next time.</summary>
    Lesson? LessonFor(string trigger, AgentContext ctx);
}

public sealed record HarnessOptions(int MaxIters, double Threshold, int RetrieveTopK)
{
    public static HarnessOptions FromEnvironment()
    {
        static int I(string k, int d) => int.TryParse(Environment.GetEnvironmentVariable(k), out var v) ? v : d;
        static double D(string k, double d) => double.TryParse(Environment.GetEnvironmentVariable(k), out var v) ? v : d;
        return new HarnessOptions(
            MaxIters: Math.Max(1, I("S2_AGENT_MAX_ITERS", 3)),
            Threshold: D("S2_AGENT_THRESHOLD", 0.80),
            RetrieveTopK: Math.Max(1, I("S2_AGENT_RETRIEVE_TOPK", 3)));
    }
}

/// <summary>What one run of the loop produced, with the telemetry that proves it compounds.</summary>
public sealed record HarnessOutcome(
    string? BestDraft,
    Reward Best,
    int Iterations,
    double FirstScore,
    IReadOnlyList<string> InjectedLessons,
    IReadOnlyList<string> LearnedLessons);

/// <summary>
/// The fast loop. Improves WITHIN a run (revise on critique) and RUN-TO-RUN (episodic memory).
/// No GPU, no weight updates — this is the Foundry-only rung. The slow loop (ART) reuses the exact
/// same <see cref="Reward"/> offline; it is not needed for the agent to get better.
/// </summary>
public sealed class AgentHarness
{
    private readonly ILessonStore _memory;
    private readonly Func<string> _clock; // injectable for determinism/testing

    public AgentHarness(ILessonStore memory, Func<string>? clock = null)
    {
        _memory = memory;
        _clock = clock ?? (() => DateTime.UtcNow.ToString("yyyy-MM-dd"));
    }

    public async Task<HarnessOutcome> RunAsync(IAgent agent, AgentContext ctx, HarnessOptions opt, CancellationToken ct)
    {
        // ── retrieve: scoped, top-k by hit-rate — never dump the whole memory ──
        var injected = await _memory.RetrieveAsync(agent.Id, ctx.Features, opt.RetrieveTopK, ct);

        string? bestDraft = null;
        Reward? best = null;
        Reward? firstReward = null;
        string? critique = null, prior = null;
        var rounds = 0;

        for (var iter = 0; iter < opt.MaxIters; iter++)
        {
            rounds++;
            var draft = await agent.GenerateAsync(ctx, injected, critique, prior, iter, ct);
            var r = agent.Evaluate(draft, ctx);
            firstReward ??= r;

            if (best is null || r.Score > best.Score) { best = r; bestDraft = draft; }
            if (best.Pass && best.Score >= opt.Threshold) break;
            if (iter == opt.MaxIters - 1) break;

            critique = r.Critique; // revise from the same draft with its fix-list
            prior = draft;
        }

        // ── write-back: record each injected lesson's outcome (did it prevent its own mistake?) ──
        var firstFails = firstReward!.FailedTriggers;
        foreach (var l in injected)
            await _memory.RecordApplicationAsync(l.Id, helped: !firstFails.Contains(l.Trigger), ct);

        // ── learn: for every mistake attempt-1 made that the loop then FIXED, mint a lesson ──
        var learned = new List<string>();
        var fixedTriggers = firstReward.FailedTriggers.Except(best!.FailedTriggers);
        foreach (var trigger in fixedTriggers)
        {
            var lesson = agent.LessonFor(trigger, ctx);
            if (lesson is null) continue;
            lesson.Date = _clock();
            await _memory.WriteAsync(lesson, ct);
            learned.Add(lesson.Id);
        }

        return new HarnessOutcome(
            bestDraft, best, rounds, firstReward.Score,
            injected.Select(l => l.Id).ToList(), learned);
    }
}
