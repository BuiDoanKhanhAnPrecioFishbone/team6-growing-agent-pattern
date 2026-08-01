namespace AIAssistant.Harness;

// ─────────────────────────────────────────────────────────────────────────────
// IMPLICIT-SIGNAL REWARD ADAPTER — how a growing agent learns when NOBODY teaches it.
//
// In a real product users don't fill in a "teach the AI" box; they just chat and
// use features. But every ordinary interaction still carries a reward signal:
//   • they EDIT the AI's output      → the edit is a correction (the richest signal)
//   • they hit REGENERATE            → a rejection, no correction attached
//   • they THUMBS-DOWN               → a rejection, maybe with a reason
//   • they ACCEPT / copy / THUMBS-UP → an endorsement
//   • a TOOL/EXECUTION call FAILS    → ground-truth negative from the environment
//
// This adapter turns those events into the SAME Reward the harness already speaks,
// and mints Provisional lessons from the negative ones — so the agent grows from
// the exhaust of normal usage. Auto-mined lessons start Provisional (tried, not
// believed); they earn Verified only via hit-rate, exactly like the fast loop's.
// Human teaching (see the Teach flow) becomes just the rarest, highest-trust signal.
//
// BCL-only. Rule-derivation from an edit is optional (a delegate) so the core stays
// LLM-free and inert offline; wire DefaultRuleDeriver() to get a general rule instead
// of a verbatim correction.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>The kind of ordinary interaction we captured. Negatives mint lessons; positives reinforce.</summary>
public enum SignalKind { Edit, Regenerate, ThumbsDown, ThumbsUp, Accept, ToolError }

/// <summary>
/// One ordinary product interaction, re-read as a training signal. <see cref="Output"/> is what the AI
/// produced; <see cref="Correction"/> is the user's edited version (Edit), the error text (ToolError), or a
/// thumbs-down reason — empty when the signal carries no correction (Regenerate, bare ThumbsDown/Up/Accept).
/// </summary>
public sealed record ImplicitSignal(
    SignalKind Kind,
    string Task,
    string Output,
    string? Correction = null,
    AgentFeatures? Features = null);

/// <summary>Maps an <see cref="ImplicitSignal"/> to the harness's <see cref="Reward"/> — so implicit feedback
/// flows into the exact same accounting (hit-rate write-back, the flywheel export) as an explicit grader.</summary>
public static class ImplicitReward
{
    /// <summary>Is this signal an endorsement (raise trust) or a correction (mint a lesson)?</summary>
    public static bool IsPositive(SignalKind k) => k is SignalKind.ThumbsUp or SignalKind.Accept;

    /// <summary>The trigger key a negative signal learns under — stable, so re-hits upsert the same lesson.</summary>
    public static string Trigger(SignalKind k) => k switch
    {
        SignalKind.Edit       => "user-edited",
        SignalKind.Regenerate => "user-regenerated",
        SignalKind.ThumbsDown => "thumbs-down",
        SignalKind.ToolError  => "tool-error",
        _                     => "endorsed",
    };

    /// <summary>
    /// Graded score for the signal. Positives → 1.0 (Pass). For an Edit we ground the score in HOW MUCH the
    /// user changed (token overlap): a tiny tweak barely dents it, a rewrite tanks it — so the reward
    /// magnitude tracks the size of the correction rather than being a flat penalty.
    /// </summary>
    public static Reward ToReward(ImplicitSignal s)
    {
        if (IsPositive(s.Kind))
            return new Reward(true, 1.0,
                new Dictionary<string, double> { ["endorsed"] = 1.0 },
                new HashSet<string>(), "User endorsed this output.");

        double score = s.Kind switch
        {
            SignalKind.Edit       => 0.30 + 0.60 * Overlap(s.Output, s.Correction), // kept-a-lot ⇒ milder
            SignalKind.Regenerate => 0.20,
            SignalKind.ThumbsDown => 0.05,
            SignalKind.ToolError  => 0.00,
            _                     => 0.10,
        };
        var trig = Trigger(s.Kind);
        var critique = s.Kind switch
        {
            SignalKind.Edit      => "User edited the output. Match the edited version's choices next time.",
            SignalKind.ToolError => "The action failed: " + Clip(s.Correction, 200),
            _ when !string.IsNullOrWhiteSpace(s.Correction) => s.Correction!.Trim(),
            SignalKind.Regenerate => "User rejected this output and asked for another.",
            _                     => "User signalled the output was not good enough.",
        };
        return new Reward(false, Math.Clamp(score, 0, 1),
            new Dictionary<string, double> { [trig] = score },
            new HashSet<string> { trig }, critique);
    }

    // crude word-overlap (Jaccard on lowercased tokens) — enough to size an edit without a tokenizer.
    private static double Overlap(string a, string? b)
    {
        if (string.IsNullOrWhiteSpace(b)) return 0;
        var sa = Tokens(a); var sb = Tokens(b!);
        if (sa.Count == 0 || sb.Count == 0) return 0;
        var inter = sa.Count(sb.Contains);
        var union = sa.Count + sb.Count - inter;
        return union == 0 ? 0 : (double)inter / union;
    }
    private static HashSet<string> Tokens(string s) =>
        s.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToHashSet();
    private static string Clip(string? s, int n) { s = (s ?? "").Replace('\n', ' ').Trim(); return s.Length <= n ? s : s[..n] + "…"; }
}

/// <summary>
/// Ingests an <see cref="ImplicitSignal"/> into the same <see cref="ILessonStore"/> the harness reads from —
/// the production counterpart of the fast loop's learn step. Negative signals mint a Provisional lesson;
/// positive signals reinforce whatever was last injected. No teaching UI required.
/// </summary>
public static class ImplicitLearner
{
    /// <summary>An LLM rule-deriver that turns an edit's before/after into ONE general imperative rule, or
    /// null offline. Wire this so the lesson generalises ("lead with the recommendation") instead of
    /// memorising this one output. Mirrors <see cref="Context.LlmSummarizer"/>.</summary>
    public static Func<ImplicitSignal, CancellationToken, Task<string?>>? DefaultRuleDeriver() =>
        ToolLoop.Enabled
            ? async (s, ct) =>
            {
                var prompt =
                    $"TASK the assistant was doing:\n{s.Task}\n\n" +
                    $"What the assistant wrote:\n{s.Output}\n\n" +
                    $"What the user changed it to:\n{s.Correction}\n\n" +
                    "State the ONE general rule (a single imperative sentence, max 20 words) that would make " +
                    "the assistant produce the user's version next time. Generalise beyond this example. No preamble.";
                var rule = await ToolLoop.CompleteAsync("You distil user edits into reusable writing/behaviour rules.", prompt, 0, ct);
                return string.IsNullOrWhiteSpace(rule) ? null : rule.Trim();
            }
        : null;

    /// <summary>
    /// Learn from one signal. Returns the lesson written (negative signals) or null (positive signals, or a
    /// signal too thin to learn from). The lesson is <see cref="Trust.Provisional"/> — it must earn hit-rate
    /// before it's trusted, so a noisy edit can't poison behaviour. For positive signals, pass
    /// <paramref name="lastInjected"/> to credit the lessons that produced the endorsed output.
    /// </summary>
    public static async Task<Lesson?> LearnAsync(
        ImplicitSignal s, ILessonStore store, string agent,
        Func<ImplicitSignal, CancellationToken, Task<string?>>? deriveRule = null,
        IReadOnlyList<Lesson>? lastInjected = null,
        Func<string>? clock = null, CancellationToken ct = default)
    {
        var now = (clock ?? (() => DateTime.UtcNow.ToString("yyyy-MM-dd")))();

        // Positive: don't mint — reinforce the lessons that led here so they climb toward Verified.
        if (ImplicitReward.IsPositive(s.Kind))
        {
            if (lastInjected is not null)
                foreach (var l in lastInjected) await store.RecordApplicationAsync(l.Id, helped: true, ct);
            return null;
        }

        var sector = s.Features?.Sector ?? "*";
        var condition = string.IsNullOrWhiteSpace(s.Features?.Situation) ? Clip(s.Task, 160) : s.Features!.Situation;

        // The Warning is the guidance injected next time. For an edit, prefer a DERIVED general rule; fall
        // back to the verbatim correction offline. For a tool error, the error itself is the guidance.
        string warning;
        if (s.Kind == SignalKind.Edit)
        {
            string? rule = deriveRule is not null ? await SafeDerive(deriveRule, s, ct) : null;
            warning = rule ?? ("Prefer this form: " + Clip(s.Correction, 240));
        }
        else if (s.Kind == SignalKind.ToolError)
            warning = "This approach failed — handle/avoid it: " + Clip(s.Correction, 240);
        else if (!string.IsNullOrWhiteSpace(s.Correction))
            warning = s.Correction!.Trim();
        else if (s.Kind == SignalKind.Regenerate)
            warning = "A previous answer of this kind was rejected — vary the approach, don't repeat it.";
        else
            warning = "A previous answer of this kind was marked unhelpful — raise the bar.";

        var trigger = ImplicitReward.Trigger(s.Kind);
        var lesson = new Lesson
        {
            Id = $"{agent}|{sector}|{trigger}:{Stable(condition)}",  // upsert key: re-hits refresh, keep stats
            Agent = agent, Sector = sector, Trigger = trigger,
            Condition = condition, Warning = warning,
            Type = s.Kind == SignalKind.ToolError ? LessonType.ToolTip : LessonType.Strategy,
            Trust = Trust.Provisional,                    // auto-mined ⇒ must earn trust (unlike human Teach)
            LearnedFrom = "implicit:" + s.Kind.ToString().ToLowerInvariant(),
            Date = now, LastUsed = now,
        };
        await store.WriteAsync(lesson, ct);
        return lesson;
    }

    private static async Task<string?> SafeDerive(Func<ImplicitSignal, CancellationToken, Task<string?>> f, ImplicitSignal s, CancellationToken ct)
    { try { return await f(s, ct); } catch { return null; } }

    // Deterministic short hash so the same condition upserts one lesson (no Guid ⇒ edits on the same task merge).
    private static string Stable(string s)
    {
        unchecked { uint h = 2166136261; foreach (var c in s.Trim().ToLowerInvariant()) { h ^= c; h *= 16777619; } return h.ToString("x8"); }
    }
    private static string Clip(string? s, int n) { s = (s ?? "").Replace('\n', ' ').Trim(); return s.Length <= n ? s : s[..n] + "…"; }
}
