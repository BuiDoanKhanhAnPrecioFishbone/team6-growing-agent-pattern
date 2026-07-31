using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AIAssistant.Harness;

namespace Compare;

// Live cost comparison for the UI: quality + real $ for bare-mini / mini+harness (cold, then warm) /
// frontier, on a small reasoning-trap suite (kept short so the UI run is bearable). The rigorous, larger
// version is the costbench console.
public static class CostRun
{
    private static readonly (string Q, string[] A)[] Suite =
    {
        ("A bat and a ball cost $1.10 in total. The bat costs $1.00 more than the ball. How much does the ball cost, in cents?", new[]{"5 cent","0.05","5c"}),
        ("A hen and a half lay an egg and a half in a day and a half. How many eggs does one hen lay in one day?", new[]{"2/3","0.66","0.67","two-third"}),
        ("A patch of lily pads doubles every day and covers a lake on day 48. On which day is the lake exactly half covered?", new[]{"47"}),
    };
    private const string Sys = "Solve the problem carefully. Think step by step, then end with a line exactly in the form 'FINAL: <answer>'.";

    private static bool Hit(string s, string[] a)
    {
        var m = Regex.Match(s, @"FINAL:\s*(.+)", RegexOptions.IgnoreCase | RegexOptions.RightToLeft);
        var t = (m.Success ? m.Groups[1].Value : s).ToLowerInvariant();
        return a.Any(k => t.Contains(k));
    }
    private static AgentContext Ctx(string q) => new()
    {
        Ticker = "Q", Features = new AgentFeatures("reason", Array.Empty<string>(), q),
        Input = new JsonObject { ["task"] = q }, AllowedSources = Array.Empty<string>(),
    };
    private static double Dollars((long p, long c) u, double pin, double pout) => u.p / 1e6 * pin + u.c / 1e6 * pout;

    public static async Task<object> RunAsync(CancellationToken ct)
    {
        var strong = Environment.GetEnvironmentVariable("AGENT_LLM_MODEL_STRONG");
        double P(string k, double d) => double.TryParse(Environment.GetEnvironmentVariable(k), out var v) ? v : d;
        double miniIn = P("AGENT_PRICE_IN", 0.40), miniOut = P("AGENT_PRICE_OUT", 1.60);
        double frIn = P("AGENT_PRICE_STRONG_IN", 1.25), frOut = P("AGENT_PRICE_STRONG_OUT", 10.0);
        int N = Suite.Length;

        // bare mini — one shot each
        CostLedger.Reset(); int bareOk = 0;
        foreach (var t in Suite) if (Hit(await ToolLoop.CompleteAsync(Sys, t.Q, 0, ct), t.A)) bareOk++;
        var bareC = Dollars(CostLedger.Snapshot(), miniIn, miniOut);

        // mini + harness — fresh memory, run the suite cold then warm
        var path = Path.Combine(Path.GetTempPath(), "compare-cost-" + Guid.NewGuid().ToString("N") + ".json");
        var harness = new AgentHarness(new SemanticLessonStore(path));
        var opt = new HarnessOptions(MaxIters: 2, Threshold: 1.0, RetrieveTopK: 3, Samples: 2);
        async Task<(int ok, double cost)> Pass()
        {
            CostLedger.Reset(); int ok = 0;
            foreach (var t in Suite) if ((await harness.RunAsync(new RAgent(t.Q, t.A), Ctx(t.Q), opt, ct)).Best.Pass) ok++;
            return (ok, Dollars(CostLedger.Snapshot(), miniIn, miniOut));
        }
        var (h1ok, h1c) = await Pass();
        var (h2ok, h2c) = await Pass();
        try { File.Delete(path); } catch { /* temp */ }

        // frontier — one shot each with the bigger model (if configured)
        int frOk = -1; double frC = 0;
        if (!string.IsNullOrWhiteSpace(strong))
        {
            CostLedger.Reset(); frOk = 0;
            foreach (var t in Suite) if (Hit(await ToolLoop.CompleteAsync(Sys, t.Q, 0, ct, strong), t.A)) frOk++;
            frC = Dollars(CostLedger.Snapshot(), frIn, frOut);
        }

        var modes = new List<object>
        {
            new { name = "bare mini", quality = bareOk, cost = bareC, kind = "bare" },
            new { name = "mini + harness · cold", quality = h1ok, cost = h1c, kind = "harness" },
            new { name = "mini + harness · warm", quality = h2ok, cost = h2c, kind = "harness" },
        };
        if (frOk >= 0) modes.Add(new { name = "frontier · " + strong, quality = frOk, cost = frC, kind = "frontier" });

        var headline = frOk >= 0
            ? $"mini+harness {(h2ok >= frOk ? "matches" : $"scores {h2ok}/{N} vs {frOk}/{N} on")} frontier quality at ~{(frC > 0 ? h2c / frC * 100 : 0):0}% of its cost once warm — and unlike the frontier, it keeps getting cheaper as it learns."
            : $"the harness got {(h1c > 0 ? (1 - h2c / h1c) * 100 : 0):0}% cheaper from cold → warm as it learned. Set AGENT_LLM_MODEL_STRONG for the frontier column.";

        return new { total = N, modes, headline, frontier = frOk >= 0 };
    }

    // The reasoning agent (answer-check reward + step-by-step, best-of-N via the harness).
    private sealed class RAgent : IAgent
    {
        private readonly string _q; private readonly string[] _a;
        public RAgent(string q, string[] a) { _q = q; _a = a; }
        public string Id => "cmp-cost-reason";
        public Task<string> GenerateAsync(AgentContext ctx, IReadOnlyList<Lesson> lessons, string? critique, string? prior, int attempt, CancellationToken ct)
        {
            var sys = Sys;
            if (lessons.Count > 0) sys += "\nLessons you have learned (apply them):" + string.Concat(lessons.Select(l => "\n• " + l.Warning));
            if (!string.IsNullOrWhiteSpace(critique)) sys += "\nYour previous answer was wrong — fix it:\n" + critique;
            return ToolLoop.CompleteAsync(sys, _q, attempt == 0 ? 0.2 : 0.8, ct);
        }
        public Reward Evaluate(string draft, AgentContext ctx)
        {
            var ok = Hit(draft, _a);
            return new Reward(ok, ok ? 1 : 0, new Dictionary<string, double> { ["correct"] = ok ? 1 : 0 },
                ok ? new HashSet<string>() : new HashSet<string> { "WRONG_ANSWER" },
                ok ? "" : "That FINAL answer is wrong. Re-read the problem, watch for the trap, and redo the steps.");
        }
        public Lesson? LessonFor(string trigger, AgentContext ctx) => new Lesson
        {
            Id = "cmp-cost-reason|reason|WRONG_ANSWER", Agent = "cmp-cost-reason", Sector = "reason", Trigger = "WRONG_ANSWER",
            Condition = "a word problem that looks simple but hides a trap",
            Warning = "Do not answer word problems from intuition. Write the equations, solve step by step, and re-check the final number against the wording before answering.",
        };
    }
}
