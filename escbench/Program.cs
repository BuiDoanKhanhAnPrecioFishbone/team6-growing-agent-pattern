using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AIAssistant.Harness;

// ─────────────────────────────────────────────────────────────────────────────
// escbench — the real cost-optimization lever: ESCALATION. Instead of paying the
// frontier price on every call, run a CHEAP first pass and escalate to the frontier
// (gpt-5.1) ONLY on the tasks it fails (the reward decides). You reach frontier-level
// quality while paying the premium on a fraction of the workload.
//   1) always-frontier            — gpt-5.1 on everything (the expensive baseline)
//   2) bare-mini  + escalate      — one mini call; on fail → gpt-5.1
//   3) mini+harness + escalate    — the harness (reward-gated); on fail → gpt-5.1
// Mini and frontier tokens are metered separately for an exact $ figure. Live-only.
// ─────────────────────────────────────────────────────────────────────────────

if (!ToolLoop.Enabled) { Console.WriteLine("Set AGENT_LLM_* (see docs/FOUNDRY-SETUP.md) and re-run."); return; }
var frontier = Environment.GetEnvironmentVariable("AGENT_LLM_MODEL_STRONG");
if (string.IsNullOrWhiteSpace(frontier)) { Console.WriteLine("Set AGENT_LLM_MODEL_STRONG to the frontier model (e.g. gpt-5.1)."); return; }

static double P(string k, double d) => double.TryParse(Environment.GetEnvironmentVariable(k), out var v) ? v : d;
double miniIn = P("AGENT_PRICE_IN", 0.40), miniOut = P("AGENT_PRICE_OUT", 1.60);
double frIn = P("AGENT_PRICE_STRONG_IN", 1.25), frOut = P("AGENT_PRICE_STRONG_OUT", 10.0);

var suite = new (string Q, string[] A)[]
{
    ("A bat and a ball cost $1.10 in total. The bat costs $1.00 more than the ball. How much does the ball cost, in cents?", new[]{"5 cent","0.05","5c"}),
    ("If it takes 5 machines 5 minutes to make 5 widgets, how long would 100 machines take to make 100 widgets?", new[]{"5 min","5 minute"}),
    ("A hen and a half lay an egg and a half in a day and a half. How many eggs does one hen lay in one day?", new[]{"2/3","0.66","0.67","two-third","two thirds"}),
    ("A patch of lily pads doubles in size every day and covers a whole lake on day 48. On which day was the lake exactly half covered?", new[]{"47"}),
    ("What number comes next in the sequence: 2, 6, 12, 20, 30, ?", new[]{"42"}),
    ("A farmer has 17 sheep. All but 9 run away. How many sheep does the farmer have left?", new[]{"9 sheep","nine","are 9","is 9"}),
    ("There are 12 fish in a tank. 5 of them drown. How many fish are left in the tank?", new[]{"12 fish","twelve","all 12","are 12"}),
    ("You are running a race and you overtake the person in 2nd place. What place are you in now?", new[]{"second","2nd"}),
    ("A snail is at the bottom of a 10-foot well. Each day it climbs 3 feet; each night it slips back 2 feet. How many days to get out?", new[]{"8 day","eight day"}),
    ("If 2 cats catch 2 mice in 2 minutes, how many cats are needed to catch 100 mice in 100 minutes?", new[]{"2 cat","two cat","just 2","only 2"}),
    ("What number comes next: 1, 1, 2, 3, 5, 8, ?", new[]{"13"}),
    ("If 8 workers build a wall in 10 hours, how long would 4 workers take to build the same wall?", new[]{"20 hour","20 hr","20h"}),
    ("A red house is made of red bricks and a blue house of blue bricks. What is a greenhouse made of?", new[]{"glass"}),
    ("Which is heavier: a pound of feathers or a pound of bricks?", new[]{"same","equal","neither","weigh the same"}),
    ("A clock takes 6 seconds to strike 6 o'clock. How many seconds does it take to strike 12 o'clock?", new[]{"13.2","13 sec"}),
};
int N = suite.Length;
const string Sys = "Solve the problem carefully. Think step by step, then end with a line exactly in the form 'FINAL: <answer>'.";

static bool Hit(string s, string[] a)
{
    var m = Regex.Match(s, @"FINAL:\s*(.+)", RegexOptions.IgnoreCase | RegexOptions.RightToLeft);
    var t = (m.Success ? m.Groups[1].Value : s).ToLowerInvariant();
    return a.Any(k => t.Contains(k));
}
static AgentContext Ctx(string q) => new()
{
    Ticker = "Q", Features = new AgentFeatures("reason", Array.Empty<string>(), q),
    Input = new JsonObject { ["task"] = q }, AllowedSources = Array.Empty<string>(),
};
double Mini((long p, long c) u) => u.p / 1e6 * miniIn + u.c / 1e6 * miniOut;
double Fr((long p, long c) u) => u.p / 1e6 * frIn + u.c / 1e6 * frOut;

Console.WriteLine($"escbench · mini=gpt-4.1-mini · frontier={frontier} · {N} reasoning traps\n");

// 1) always-frontier
CostLedger.Reset(); int fQ = 0;
foreach (var t in suite) if (Hit(await ToolLoop.CompleteAsync(Sys, t.Q, 0, default, frontier), t.A)) fQ++;
double fCost = Fr(CostLedger.Snapshot());

// 2) bare-mini + escalate-on-fail
double bMini = 0, bFr = 0; int bQ = 0, bEsc = 0;
foreach (var t in suite)
{
    CostLedger.Reset(); var a = await ToolLoop.CompleteAsync(Sys, t.Q, 0); bMini += Mini(CostLedger.Snapshot());
    if (Hit(a, t.A)) bQ++;
    else { bEsc++; CostLedger.Reset(); var f = await ToolLoop.CompleteAsync(Sys, t.Q, 0, default, frontier); bFr += Fr(CostLedger.Snapshot()); if (Hit(f, t.A)) bQ++; }
}

// 3) mini+harness + escalate-on-fail (reward gates the escalation)
var harness = new AgentHarness(new SemanticLessonStore(Path.Combine(Path.GetTempPath(), "escbench.json")));
var opt = new HarnessOptions(MaxIters: 2, Threshold: 1.0, RetrieveTopK: 3, Samples: 1); // cheap config: revise + memory, no best-of-N
double hMini = 0, hFr = 0; int hQ = 0, hEsc = 0;
foreach (var t in suite)
{
    CostLedger.Reset(); var o = await harness.RunAsync(new RAgent(t.Q, t.A), Ctx(t.Q), opt, default); hMini += Mini(CostLedger.Snapshot());
    if (o.Best.Pass) hQ++;
    else { hEsc++; CostLedger.Reset(); var f = await ToolLoop.CompleteAsync(Sys, t.Q, 0, default, frontier); hFr += Fr(CostLedger.Snapshot()); if (Hit(f, t.A)) hQ++; }
}

double bCost = bMini + bFr, hCost = hMini + hFr;
string Row(string name, int q, double cost, int esc) =>
    $"{name,-28} {q}/{N,-4} ${cost:0.00000}  {(fCost > 0 ? cost / fCost * 100 : 0),4:0}% of frontier   escalated {esc}/{N}";

Console.WriteLine($"{"strategy",-28} {"qual",-5} {"cost $",-11} {"vs frontier",-15} premium calls");
Console.WriteLine(new string('─', 82));
Console.WriteLine(Row("always-frontier (gpt-5.1)", fQ, fCost, N));
Console.WriteLine(Row("bare-mini + escalate", bQ, bCost, bEsc));
Console.WriteLine(Row("mini+harness + escalate", hQ, hCost, hEsc));
Console.WriteLine();

var best = bCost <= hCost ? ("bare-mini + escalate", bQ, bCost, bEsc) : ("mini+harness + escalate", hQ, hCost, hEsc);
Console.WriteLine($"→ {best.Item1} reaches {best.Item2}/{N} (frontier: {fQ}/{N}) at {(fCost > 0 ? best.Item3 / fCost * 100 : 0):0}% of always-frontier cost —");
Console.WriteLine($"  paying the gpt-5.1 premium on only {best.Item4}/{N} tasks. That is what cost-optimized means.");

sealed class RAgent : IAgent
{
    private readonly string _q; private readonly string[] _a;
    public RAgent(string q, string[] a) { _q = q; _a = a; }
    public string Id => "escbench-reason";
    public Task<string> GenerateAsync(AgentContext ctx, IReadOnlyList<Lesson> lessons, string? critique, string? prior, int attempt, CancellationToken ct)
    {
        var sys = "Solve the problem carefully. Think step by step, then end with a line exactly in the form 'FINAL: <answer>'.";
        if (lessons.Count > 0) sys += "\nLessons (apply):" + string.Concat(lessons.Select(l => "\n• " + l.Warning));
        if (!string.IsNullOrWhiteSpace(critique)) sys += "\nPrevious answer was wrong — fix it:\n" + critique;
        return ToolLoop.CompleteAsync(sys, _q, 0, ct);
    }
    public Reward Evaluate(string draft, AgentContext ctx)
    {
        var m = Regex.Match(draft, @"FINAL:\s*(.+)", RegexOptions.IgnoreCase | RegexOptions.RightToLeft);
        var tail = (m.Success ? m.Groups[1].Value : draft).ToLowerInvariant();
        var ok = _a.Any(k => tail.Contains(k));
        return new Reward(ok, ok ? 1 : 0, new Dictionary<string, double> { ["correct"] = ok ? 1 : 0 },
            ok ? new HashSet<string>() : new HashSet<string> { "WRONG_ANSWER" },
            ok ? "" : "That FINAL answer is wrong. Re-read the problem, watch for the trap, and redo the steps.");
    }
    public Lesson? LessonFor(string trigger, AgentContext ctx) => trigger != "WRONG_ANSWER" ? null : new Lesson
    {
        Id = "escbench-reason|reason|WRONG_ANSWER", Agent = "escbench-reason", Sector = "reason", Trigger = "WRONG_ANSWER",
        Condition = "a tricky word problem",
        Warning = "Do not answer from intuition. Write the equations, solve step by step, and re-check the final number against the wording.",
    };
}
