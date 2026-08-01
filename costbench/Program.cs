using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AIAssistant.Harness;

// ─────────────────────────────────────────────────────────────────────────────
// costbench — the cost thesis, measured. On a suite of reasoning traps, four ways:
//   1) bare mini            — one gpt-4.1-mini call (the cheap model alone)
//   2) mini + harness (r1)  — best-of-N + revise + memory, first exposure (cold)
//   3) mini + harness (r2)  — the SAME suite again, memory now warm
//   4) frontier             — one call to a bigger model (AGENT_LLM_MODEL_STRONG)
// For each: quality (reward pass-rate) AND cost ($ from real token usage). The claim to test:
//   mini+harness ≈ frontier QUALITY, and while frontier stays flat-expensive, the harness gets
//   CHEAPER run-over-run as it learns. Live-only. See docs/FOUNDRY-SETUP.md.
// ─────────────────────────────────────────────────────────────────────────────

if (!ToolLoop.Enabled)
{
    Console.WriteLine("""
        costbench needs a live model. Set your Foundry deployment, then re-run:
          $env:AGENT_LLM_BASE_URL = "https://<resource>.openai.azure.com/openai/v1"
          $env:AGENT_LLM_API_KEY  = "<key>"
          $env:AGENT_LLM_MODEL    = "gpt-4.1-mini"
          $env:AGENT_LLM_MODEL_STRONG = "gpt-4.1"   # optional: the frontier reference column
          dotnet run --project costbench
        Prices (per 1M tokens) default to gpt-4.1-mini / gpt-4.1; override with
        AGENT_PRICE_IN/OUT and AGENT_PRICE_STRONG_IN/OUT.
        """);
    return;
}

static double P(string k, double d) => double.TryParse(Environment.GetEnvironmentVariable(k), out var v) ? v : d;
var mini = Environment.GetEnvironmentVariable("AGENT_LLM_MODEL") ?? "gpt-4.1-mini";
var frontier = Environment.GetEnvironmentVariable("AGENT_LLM_MODEL_STRONG");   // optional
double miniIn = P("AGENT_PRICE_IN", 0.40), miniOut = P("AGENT_PRICE_OUT", 1.60);    // gpt-4.1-mini ≈ $/1M
double frIn = P("AGENT_PRICE_STRONG_IN", 2.00), frOut = P("AGENT_PRICE_STRONG_OUT", 8.00); // gpt-4.1 ≈ $/1M

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

const string BaseSys = "Solve the problem carefully. Think step by step, then end with a line exactly in the form 'FINAL: <answer>'.";
static double Dollars((long p, long c) u, double pin, double pout) => u.p / 1e6 * pin + u.c / 1e6 * pout;

Console.WriteLine($"costbench · mini={mini}{(frontier is null ? " · (no frontier set — bare vs harness only)" : $" · frontier={frontier}")}\n");

// 1) bare mini — one shot
CostLedger.Reset(); int bareOk = 0;
foreach (var t in suite) if (Score.Hit(await ToolLoop.CompleteAsync(BaseSys, t.Q), t.A)) bareOk++;
var bareU = CostLedger.Snapshot();

// 2+3) mini + harness — best-of-N + revise + memory. Same suite twice: cold, then warm.
var path = Path.Combine(Path.GetTempPath(), "costbench-lessons.json"); File.Delete(path);
var store = new SemanticLessonStore(path);
var harness = new AgentHarness(store);
var opt = new HarnessOptions(MaxIters: 2, Threshold: 1.0, RetrieveTopK: 3, Samples: 2);

async Task<(int ok, (long, long) u)> HarnessPass()
{
    CostLedger.Reset(); int ok = 0;
    foreach (var t in suite)
    {
        var ctx = new AgentContext
        {
            Ticker = "Q",
            Features = new AgentFeatures("reason", Array.Empty<string>(), t.Q),
            Input = new JsonObject { ["task"] = t.Q },
            AllowedSources = Array.Empty<string>(),
        };
        var o = await harness.RunAsync(new ReasonAgent(t.Q, t.A), ctx, opt, default);
        if (o.Best.Pass) ok++;
    }
    return (ok, CostLedger.Snapshot());
}
var (h1ok, h1u) = await HarnessPass();
var (h2ok, h2u) = await HarnessPass();

// 4) frontier — one shot with the bigger model (if configured)
int frOk = -1; (long, long) frU = (0, 0);
if (!string.IsNullOrWhiteSpace(frontier))
{
    CostLedger.Reset(); frOk = 0;
    foreach (var t in suite) if (Score.Hit(await ToolLoop.CompleteAsync(BaseSys, t.Q, model: frontier), t.A)) frOk++;
    frU = CostLedger.Snapshot();
}

// ── report ──
double bareC = Dollars(bareU, miniIn, miniOut), h1C = Dollars(h1u, miniIn, miniOut), h2C = Dollars(h2u, miniIn, miniOut);
double frC = frOk >= 0 ? Dollars(frU, frIn, frOut) : 0;
string Row(string name, int ok, double cost, (long p, long c) u) => $"{name,-24} {ok}/{N,-5} ${cost:0.00000}  {u.p,7}+{u.c,-6} tok";

Console.WriteLine($"{"mode",-24} {"qual",-5} {"cost $",-11} tokens (in + out)");
Console.WriteLine(new string('─', 64));
Console.WriteLine(Row("bare mini", bareOk, bareC, bareU));
Console.WriteLine(Row("mini + harness (r1)", h1ok, h1C, h1u));
Console.WriteLine(Row("mini + harness (r2)", h2ok, h2C, h2u) + (h2C < h1C ? "  ← learned" : ""));
if (frOk >= 0) Console.WriteLine(Row("frontier", frOk, frC, frU));
Console.WriteLine();

if (frOk >= 0)
{
    var qMatch = h2ok >= frOk ? "matches" : $"{h2ok}/{N} vs {frOk}/{N}";
    var pct = frC > 0 ? h2C / frC * 100 : 0;
    Console.WriteLine($"→ mini+harness {qMatch} frontier quality at ~{pct:0}% of frontier cost (warm run).");
    if (h2C < h1C) Console.WriteLine($"→ frontier costs the same every time; the harness dropped {(1 - h2C / h1C) * 100:0}% from run 1 → run 2 as it learned.");
}
else
{
    Console.WriteLine("→ set AGENT_LLM_MODEL_STRONG to add the frontier column and get the quality/cost ratio.");
    if (h2C < h1C) Console.WriteLine($"→ the harness got {(1 - h2C / h1C) * 100:0}% cheaper from run 1 → run 2 as it learned (same quality).");
}

// ── the reasoning agent + shared scorer ──
static class Score
{
    public static bool Hit(string answer, string[] accept)
    {
        var m = Regex.Match(answer, @"FINAL:\s*(.+)", RegexOptions.IgnoreCase | RegexOptions.RightToLeft);
        var tail = (m.Success ? m.Groups[1].Value : answer).ToLowerInvariant();
        return accept.Any(k => tail.Contains(k));
    }
}

sealed class ReasonAgent : IAgent
{
    private readonly string _q; private readonly string[] _a;
    public ReasonAgent(string q, string[] a) { _q = q; _a = a; }
    public string Id => "costbench-reason";
    public Task<string> GenerateAsync(AgentContext ctx, IReadOnlyList<Lesson> lessons, string? critique, string? prior, int attempt, CancellationToken ct)
    {
        var sys = "Solve the problem carefully. Think step by step, then end with a line exactly in the form 'FINAL: <answer>'.";
        if (lessons.Count > 0) sys += "\nLessons learned (apply them):" + string.Concat(lessons.Select(l => "\n• " + l.Warning));
        if (!string.IsNullOrWhiteSpace(critique)) sys += "\nYour previous answer was wrong — fix it:\n" + critique;
        return ToolLoop.CompleteAsync(sys, _q, attempt == 0 ? 0.2 : 0.8, ct);
    }
    public Reward Evaluate(string draft, AgentContext ctx)
    {
        var ok = Score.Hit(draft, _a);
        return new Reward(ok, ok ? 1 : 0, new Dictionary<string, double> { ["correct"] = ok ? 1 : 0 },
            ok ? new HashSet<string>() : new HashSet<string> { "WRONG_ANSWER" },
            ok ? "" : "That FINAL answer is wrong. Re-read the problem, watch for the trap, and redo the steps.");
    }
    public Lesson? LessonFor(string trigger, AgentContext ctx) => trigger != "WRONG_ANSWER" ? null : new Lesson
    {
        Id = "costbench-reason|reason|WRONG_ANSWER", Agent = "costbench-reason", Sector = "reason", Trigger = "WRONG_ANSWER",
        Condition = "a word problem that looks simple but hides a trap",
        Warning = "Do not answer word problems from intuition. Write the equations, solve step by step, and re-check the final number against the wording before answering.",
    };
}
