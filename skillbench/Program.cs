using AIAssistant.Harness;

// ─────────────────────────────────────────────────────────────────────────────
// skillbench — the SKILL / PROCEDURE tier. A one-line lesson is advisory; a PROCEDURE
// is a reusable METHOD that transfers to new tasks. This proves the whole mechanism
// offline & deterministically:
//   1. ExpeL contrastive   distil the procedure a PASSING answer used that a FAILING one missed
//   2. Voyager verify-gate commit it to memory ONLY if following it reproduces a pass
//   3. AWM transfer        retrieve it for a NEW, unseen task → the method carries over → it passes
// The procedure is stored as LessonType.Procedure, so it inherits trust, importance,
// corroboration-gating and audit — the skill tier is memory, not a bolt-on.
// ─────────────────────────────────────────────────────────────────────────────

const string Agent = "valuation-analyst", Sector = "advisory";
int pass = 0, fail = 0;
void Check(string name, bool ok) { Console.WriteLine($"   [{(ok ? "PASS" : "FAIL")}] {name}"); if (ok) pass++; else fail++; }

// The reward: a valuation "follows the method" iff it does the three things that matter.
static Reward Grade(string ans)
{
    var a = (ans ?? "").ToLowerInvariant();
    return Checks.Of(
        ("pulls financials",              a.Contains("financ")),
        ("computes free cash flow",       a.Contains("cash flow")),
        ("discounts to intrinsic value",  a.Contains("intrinsic")));
}
// A bare answer skips the method; a guided answer FOLLOWS the retrieved steps verbatim.
static string Bare(string co) => $"{co} looks strong — buy.";
static string Guided(string co, IReadOnlyList<string> steps) => $"For {co}, I: " + string.Join("; ", steps) + ".";

var path = Path.Combine(Path.GetTempPath(), "skillbench.json"); File.Delete(path);
var store = new SemanticLessonStore(path);

Console.WriteLine("skillbench — turning what worked into a reusable procedure\n");
Console.WriteLine($"model: {(ToolLoop.Enabled ? "live (LLM extraction)" : "offline (deterministic diff)")}\n");

// ── 1. ExpeL contrastive extraction: a pass vs a fail on task A ("value ACME") ──
Console.WriteLine("1. Contrastive extraction — distil the method the winner used");
const string taskA = "Value company ACME and give a recommendation.";
var failDraft = "ACME looks fine — buy it.";
var passDraft = "Method: 1. Pull the latest financials. 2. Compute free cash flow. 3. Discount the cash flow to an intrinsic value.";
var steps = await SkillExtractor.ContrastAsync(taskA, passDraft, failDraft);
foreach (var s in steps) Console.WriteLine($"     → {s}");
Check("extracts a multi-step procedure (≥3 steps)", steps.Count >= 3);
Check("keeps the method steps (financials / cash flow / intrinsic)",
    steps.Any(s => s.Contains("financ")) && steps.Any(s => s.Contains("cash flow")) && steps.Any(s => s.Contains("intrinsic")));

// ── 2. Voyager verify-gate: commit ONLY if following it reproduces a pass ──
Console.WriteLine("\n2. Verify-gate — commit only a procedure that actually works");
var committed = await SkillExtractor.CommitIfVerifiedAsync(
    store, Agent, Sector, situation: "valuing a company", steps, provenance: "contrast on ACME",
    verify: (st, ct) => Task.FromResult(Grade(Guided("TESTCO", st)).Pass));   // following it must pass
Check("a verified procedure is committed to memory", committed is not null);
Check("it is stored as a Procedure (rides the normal memory)", committed?.Type == LessonType.Procedure);
Check("it earned an importance score", committed is { Importance: > 0 });

// an UNVERIFIED skill must be rejected by the gate
var junk = await SkillExtractor.CommitIfVerifiedAsync(
    store, Agent, Sector, "bogus situation", new[] { "guess wildly" }, "junk",
    verify: (st, ct) => Task.FromResult(false));
Check("an unverified skill is NOT committed (library stays clean)", junk is null);

// ── 3. AWM transfer: retrieve the procedure for a NEW company and follow it ──
Console.WriteLine("\n3. Transfer — the method carries to an unseen task (ZENITH)");
var newFeatures = new AgentFeatures(Sector, Array.Empty<string>(), "valuing a company for ZENITH");
var recalled = await store.RetrieveAsync(Agent, newFeatures, topK: 3, default);
var proc = recalled.FirstOrDefault(l => l.Type == LessonType.Procedure);
Check("the procedure is recalled for the new task", proc is not null);

// follow the recalled procedure vs answer bare
var recalledSteps = proc is null ? Array.Empty<string>() : ParseBack(proc.Warning);
var bareB = Grade(Bare("ZENITH"));
var guidedB = Grade(Guided("ZENITH", recalledSteps));
Console.WriteLine($"     bare   ZENITH → {bareB.Score * 100:0}%  ({Trim(Bare("ZENITH"))})");
Console.WriteLine($"     guided ZENITH → {guidedB.Score * 100:0}%  (follows the recalled procedure)");
Check("bare answer on the new task FAILS the method", !bareB.Pass);
Check("following the recalled procedure PASSES the new task", guidedB.Pass);
Check("the skill lifted quality on an unseen task", guidedB.Score > bareB.Score);

Console.WriteLine($"\n{pass} passed, {fail} failed.");
Console.WriteLine(fail == 0
    ? "\nVerdict: what worked once became a reusable, verified method — and it transferred to a task it never saw."
    : "\nVerdict: the skill tier regressed — investigate.");
Environment.Exit(fail == 0 ? 0 : 1);

// parse the numbered steps back out of a formatted procedure block (what an agent would follow).
static IReadOnlyList<string> ParseBack(string warning) =>
    warning.Split('\n').Select(l => l.Trim())
        .Where(l => l.Length > 0 && char.IsDigit(l[0]))
        .Select(l => l.TrimStart('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.', ')', ' ').Trim())
        .ToList();
static string Trim(string s) => s.Length <= 40 ? s : s[..40] + "…";
