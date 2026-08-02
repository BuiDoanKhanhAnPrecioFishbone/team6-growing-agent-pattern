using System.Text.RegularExpressions;

namespace AIAssistant.Harness;

// ─────────────────────────────────────────────────────────────────────────────
// THE SKILL / PROCEDURE TIER — above a one-line lesson. A lesson is advisory
// ("cite a source"); a PROCEDURE is a reusable METHOD — the ordered steps that
// actually worked — which transfers to new tasks the way a rule can't (Agent
// Workflow Memory: +24–51%). Three ideas from the research, one small surface:
//
//   • ExpeL (contrastive):  distil the procedure the PASSING answer followed that a
//                           FAILING one missed — sharper than reflecting on failure alone.
//   • AWM (induction):      induce the common procedure across several successes.
//   • Voyager (verify-gate): a skill is COMMITTED only after it's verified to reproduce
//                            a pass — never add an unproven skill to the library.
//
// A procedure is stored as LessonType.Procedure, so it inherits the whole memory
// (trust, importance, corroboration-gated promotion, audit, recall) for free.
// LLM live; deterministic offline, so extraction always works.
// ─────────────────────────────────────────────────────────────────────────────
public static class SkillExtractor
{
    /// <summary>ExpeL contrastive extraction: the reusable procedure the passing draft followed and the
    /// failing one missed. Numbered imperative steps. LLM live; a deterministic diff offline.</summary>
    public static async Task<IReadOnlyList<string>> ContrastAsync(string task, string passDraft, string failDraft, CancellationToken ct = default)
    {
        if (ToolLoop.Enabled)
        {
            try
            {
                var prompt =
                    $"TASK:\n{task}\n\nAN ANSWER THAT PASSED:\n{passDraft}\n\nAN ANSWER THAT FAILED:\n{failDraft}\n\n" +
                    "Extract the reusable PROCEDURE the passing answer followed that the failing one missed. " +
                    "Reply as terse numbered steps (max 5), each a single imperative. No preamble.";
                var r = await ToolLoop.CompleteAsync("You distil what worked into a reusable step-by-step procedure.", prompt, 0, ct);
                var steps = ParseSteps(r);
                if (steps.Count > 0) return steps;
            }
            catch { /* fall through */ }
        }
        return DiffSteps(passDraft, failDraft);
    }

    /// <summary>AWM induction: the procedure common to several passing trajectories (the shared method).
    /// LLM live; offline returns the steps of the shortest success as the representative procedure.</summary>
    public static async Task<IReadOnlyList<string>> InduceAsync(IReadOnlyList<string> passingDrafts, CancellationToken ct = default)
    {
        var drafts = passingDrafts.Where(d => !string.IsNullOrWhiteSpace(d)).ToList();
        if (drafts.Count == 0) return Array.Empty<string>();
        if (ToolLoop.Enabled)
        {
            try
            {
                var listing = string.Join("\n\n---\n\n", drafts.Take(6));
                var r = await ToolLoop.CompleteAsync(
                    "You induce the common reusable procedure shared by several good answers.",
                    listing + "\n\nWrite the shared PROCEDURE as terse numbered steps (max 5). No preamble.", 0, ct);
                var steps = ParseSteps(r);
                if (steps.Count > 0) return steps;
            }
            catch { /* fall through */ }
        }
        return drafts.Select(SplitSteps).Where(s => s.Count > 0).OrderBy(s => s.Count).FirstOrDefault() ?? Array.Empty<string>();
    }

    /// <summary>Voyager verify-gate: commit the procedure to <paramref name="store"/> ONLY if
    /// <paramref name="verify"/> (following it reproduces a passing result) succeeds. Returns the committed
    /// lesson, or null when the skill didn't verify (so the library never fills with unproven skills).</summary>
    public static async Task<Lesson?> CommitIfVerifiedAsync(
        SemanticLessonStore store, string agent, string sector, string situation, IReadOnlyList<string> steps,
        string provenance, Func<IReadOnlyList<string>, CancellationToken, Task<bool>> verify, CancellationToken ct = default)
    {
        if (steps.Count == 0) return null;
        if (!await verify(steps, ct)) return null;         // unproven ⇒ not committed
        var lesson = ToProcedureLesson(agent, sector, situation, steps, provenance);
        await store.WriteAsync(lesson, ct);
        return lesson;
    }

    /// <summary>Wrap extracted steps as a Procedure lesson — rides the normal memory machinery.</summary>
    public static Lesson ToProcedureLesson(string agent, string sector, string situation, IReadOnlyList<string> steps, string provenance) => new()
    {
        Id = $"{agent}|{sector}|proc:{Stable(situation)}", Agent = agent, Sector = sector, Trigger = "procedure",
        Condition = situation, Warning = Format(steps), Type = LessonType.Procedure, LearnedFrom = provenance,
    };

    /// <summary>Format steps for injection as a procedure block an agent can follow.</summary>
    public static string Format(IReadOnlyList<string> steps) =>
        "Follow this proven procedure:\n" + string.Join("\n", steps.Select((s, i) => $"{i + 1}. {Clean(s)}"));

    // ── parsing helpers ──
    private static readonly Regex StepMark = new(@"^\s*(?:\d+[\.\)]|[-*•])\s*", RegexOptions.Compiled);
    private static string Clean(string s) => StepMark.Replace(s.Trim(), "").Trim();

    private static IReadOnlyList<string> ParseSteps(string text) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && StepMark.IsMatch(l))
            .Select(Clean).Where(s => s.Length > 0).ToList();

    // split a free-form draft into candidate steps: prefer explicit numbered markers, else sentences.
    private static IReadOnlyList<string> SplitSteps(string draft)
    {
        var numbered = Regex.Split(draft, @"(?=\b\d+[\.\)]\s)")
            .Select(s => s.Trim()).Where(s => StepMark.IsMatch(s)).Select(Clean).Where(s => s.Length > 2).ToList();
        if (numbered.Count > 0) return numbered;
        return draft.Split('.').Select(s => s.Trim()).Where(s => s.Length > 4).ToList();
    }

    // deterministic contrastive fallback: the steps in the passing draft that the failing draft lacks.
    private static IReadOnlyList<string> DiffSteps(string pass, string fail)
    {
        var failLc = fail.ToLowerInvariant();
        return SplitSteps(pass).Where(s => !failLc.Contains(s.ToLowerInvariant())).ToList();
    }

    private static string Stable(string s)
    { unchecked { uint h = 2166136261; foreach (var c in s.Trim().ToLowerInvariant()) { h ^= c; h *= 16777619; } return h.ToString("x8"); } }
}
