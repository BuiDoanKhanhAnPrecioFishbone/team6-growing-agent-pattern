using CurriculumNs = AIAssistant.Harness.Curriculum;
using AIAssistant.Harness;

// ─────────────────────────────────────────────────────────────────────────────
// curbench — the automatic curriculum. Proves the harness turns passive capture into
// DIRECTED practice: from recent failures + unproven / regressing lessons it ranks what
// to drill next. Deterministic (constructed signals exercise each branch).
// ─────────────────────────────────────────────────────────────────────────────

int pass = 0, fail = 0;
void Check(string name, bool ok) { Console.WriteLine($"   [{(ok ? "PASS" : "FAIL")}] {name}"); if (ok) pass++; else fail++; }

const string Agent = "analyst", Sector = "advisory";
Lesson L(string trig, Trust trust, double hit, int applied, int helpedCtx, string warn) => new()
{
    Id = $"{Agent}|{Sector}|{trig}", Agent = Agent, Sector = Sector, Trigger = trig, Condition = $"situation:{trig}",
    Warning = warn, Trust = trust, HitRate = hit, TimesApplied = applied,
    HelpedContexts = Enumerable.Range(0, helpedCtx).Select(i => $"ctx{i}").ToList(),
};

// current memory: one unproven provisional, one healthy verified, one regressing verified
var lessons = new List<Lesson>
{
    L("risk-caveat",  Trust.Provisional, 1.0, 1, 1, "Always add a risk caveat."),   // unproven — 1 more corroboration
    L("be-concise",   Trust.Verified,    0.9, 8, 3, "Keep it concise."),            // healthy — should NOT be proposed
    L("cite-source",  Trust.Verified,    0.3, 6, 3, "Cite a source."),              // regressing — hit-rate slipped
};

// recent runs: "names a figure" failed a lot; "risk-caveat" failed once
var recent = new[]
{
    Checks.Of(("names a figure", false), ("states a call", true)),
    Checks.Of(("names a figure", false), ("states a call", true)),
    Checks.Of(("names a figure", false), ("risk-caveat", false)),
};

Console.WriteLine("curbench — what should the agent practice next?\n");
var plan = CurriculumNs.Propose(lessons, recent, top: 5);
foreach (var p in plan)
    Console.WriteLine($"   [{p.Kind,-15}] p={p.Priority:0.0}  {p.Focus}  —  {p.Reason}");
Console.WriteLine();

Check("proposes the most-failed check FIRST", plan.Count > 0 && plan[0].Focus == "names a figure" && plan[0].Kind == "weak-skill");
Check("includes the unproven provisional lesson", plan.Any(p => p.Kind == "unproven-lesson" && p.Focus.Contains("risk")));
Check("includes the regressing verified lesson", plan.Any(p => p.Kind == "regressing" && p.Focus.Contains("cite")));
Check("does NOT propose the healthy verified lesson", !plan.Any(p => p.Focus.Contains("concise")));
Check("weak skills outrank memory-maintenance", plan[0].Priority > plan.Where(p => p.Kind != "weak-skill").Max(p => p.Priority));

Console.WriteLine($"\n{pass} passed, {fail} failed.");
Console.WriteLine(fail == 0
    ? "\nVerdict: the free-data exhaust becomes a practice plan — the agent improves on purpose, not just by luck."
    : "\nVerdict: the curriculum regressed — investigate.");
Environment.Exit(fail == 0 ? 0 : 1);
