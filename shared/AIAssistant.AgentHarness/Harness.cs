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
    public Trust Trust { get; set; } = Trust.Provisional;       // learned lessons start Provisional; promote on hit-rate or a human gate
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

/// <summary>
/// Loop knobs. <see cref="Samples"/> is the amplifier: per round the loop draws that many independent
/// drafts and keeps the one the reward scores highest (best-of-N / inference-time compute). Samples=1 is
/// the plain single-shot loop — the historical behaviour, so this is drop-in.
/// </summary>
public sealed record HarnessOptions(int MaxIters, double Threshold, int RetrieveTopK, int Samples = 1)
{
    public static HarnessOptions FromEnvironment()
    {
        static int I(string k, int d) => int.TryParse(Environment.GetEnvironmentVariable(k), out var v) ? v : d;
        static double D(string k, double d) => double.TryParse(Environment.GetEnvironmentVariable(k), out var v) ? v : d;
        return new HarnessOptions(
            MaxIters: Math.Max(1, I("S2_AGENT_MAX_ITERS", 3)),
            Threshold: D("S2_AGENT_THRESHOLD", 0.80),
            RetrieveTopK: Math.Max(1, I("S2_AGENT_RETRIEVE_TOPK", 3)),
            Samples: Math.Max(1, I("AGENT_SAMPLES", 1)));
    }
}

/// <summary>What one run of the loop produced, with the telemetry that proves it compounds.
/// <see cref="Generations"/> is the total model drafts spent this run (iterations × samples) — the cost
/// signal, so a caller can trade quality against spend. <see cref="Escalated"/> records whether the run
/// fell back to a stronger model.</summary>
public sealed record HarnessOutcome(
    string? BestDraft,
    Reward Best,
    int Iterations,
    double FirstScore,
    IReadOnlyList<string> InjectedLessons,
    IReadOnlyList<string> LearnedLessons,
    int Generations = 0,
    bool Escalated = false,
    IReadOnlyList<Attempt>? Attempts = null);   // every (draft, reward) this run — the training-data flywheel

/// <summary>
/// Optional self-verification (amplifier lever). An LLM critic inspects a draft the deterministic
/// <see cref="Reward"/> couldn't fault and returns concrete fixes, or <c>null</c> when it has no objection.
/// The reward stays the sole authority for scoring and selection — the critic only enriches the revision
/// signal, so a soft-quality error gets one more pass within the existing iteration budget.
/// </summary>
public interface ICritic
{
    Task<string?> CritiqueAsync(AgentContext ctx, string draft, Reward reward, CancellationToken ct);
}

/// <summary>
/// Optional escalation (amplifier lever). Called when the cheap-model loop finishes below threshold:
/// regenerate one draft with a STRONGER model — so you pay for the big model only on the hard cases.
/// Returns <c>null</c> to decline (e.g. no strong model configured). The harness evaluates the returned
/// draft with the same reward and keeps it only if it scores higher.
/// </summary>
public delegate Task<string?> EscalateDraft(
    IAgent agent, AgentContext ctx, IReadOnlyList<Lesson> lessons, string? critique, CancellationToken ct);

/// <summary>
/// The fast loop. Improves WITHIN a run (revise on critique) and RUN-TO-RUN (episodic memory).
/// No GPU, no weight updates — this is the Foundry-only rung. The slow loop (ART) reuses the exact
/// same <see cref="Reward"/> offline; it is not needed for the agent to get better.
/// </summary>
public sealed class AgentHarness
{
    private readonly ILessonStore _memory;
    private readonly Func<string> _clock; // injectable for determinism/testing
    private readonly ICritic? _critic;    // optional self-verify (null ⇒ off, deterministic)
    private readonly EscalateDraft? _escalate; // optional cheap→strong fallback (null ⇒ off)

    public AgentHarness(ILessonStore memory, Func<string>? clock = null, ICritic? critic = null, EscalateDraft? escalate = null)
    {
        _memory = memory;
        _clock = clock ?? (() => DateTime.UtcNow.ToString("yyyy-MM-dd"));
        _critic = critic;
        _escalate = escalate;
    }

    // Fold the deterministic reward's critique together with the LLM reviewer's notes into one fix-list.
    private static string? Combine(string? rewardCritique, string? reviewerNotes)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(rewardCritique)) parts.Add(rewardCritique!);
        if (!string.IsNullOrWhiteSpace(reviewerNotes)) parts.Add("Reviewer also flagged:\n" + reviewerNotes);
        return parts.Count == 0 ? null : string.Join("\n", parts);
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
        var generations = 0;
        var escalated = false;
        var attempts = new List<Attempt>();   // collect every (draft, reward) for the training-data export

        for (var iter = 0; iter < opt.MaxIters; iter++)
        {
            rounds++;

            // ── best-of-N: draw Samples independent drafts this round; the reward picks the winner. ──
            // A cheap model has a wide quality spread; sampling several and keeping the best is the
            // single biggest inference-time lift. seed == iter when Samples==1 (byte-for-byte the old loop).
            Reward? roundBest = null; string? roundBestDraft = null;
            for (var s = 0; s < opt.Samples; s++)
            {
                var seed = iter * opt.Samples + s;
                var draft = await agent.GenerateAsync(ctx, injected, critique, prior, seed, ct);
                var r = agent.Evaluate(draft, ctx);
                generations++;
                attempts.Add(new Attempt(draft, r.Score, r.Pass));
                firstReward ??= r;
                if (roundBest is null || r.Score > roundBest.Score) { roundBest = r; roundBestDraft = draft; }
            }

            // Samples ≥ 1, so roundBest is always assigned above — assert it to keep `best` non-null.
            if (best is null || roundBest!.Score > best.Score) { best = roundBest!; bestDraft = roundBestDraft; }
            var passed = best.Pass && best.Score >= opt.Threshold;

            // ── self-verify: an LLM critic may object even when the reward is satisfied (soft errors the
            //    deterministic reward can't see). Selection stays reward-only; this only shapes the revise.
            //    Skip it on the final iteration — there is no revise left to act on its feedback. ──
            string? reviewerNotes = (_critic is null || iter == opt.MaxIters - 1) ? null
                : await _critic.CritiqueAsync(ctx, roundBestDraft!, roundBest!, ct);

            if (passed && reviewerNotes is null) break;   // reward satisfied AND no reviewer objection
            if (iter == opt.MaxIters - 1) break;

            critique = Combine(passed ? null : roundBest!.Critique, reviewerNotes); // revise from the round's best draft
            prior = roundBestDraft;
        }

        // ── escalate: the cheap loop finished below the bar → one attempt with a stronger model, hard
        //    cases only. Same reward judges it; keep it only if it actually scores higher. ──
        if (_escalate is not null && !(best!.Pass && best.Score >= opt.Threshold))
        {
            var esc = await _escalate(agent, ctx, injected, critique, ct);
            if (!string.IsNullOrEmpty(esc))
            {
                var r = agent.Evaluate(esc, ctx);
                generations++;
                escalated = true;
                attempts.Add(new Attempt(esc, r.Score, r.Pass));
                if (r.Score > best.Score) { best = r; bestDraft = esc; }
            }
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
            injected.Select(l => l.Id).ToList(), learned, generations, escalated, attempts);
    }
}
