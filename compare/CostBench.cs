using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AIAssistant.Harness;

namespace Compare;

// Live cost comparison for the UI — the honest cost-optimization lever: ESCALATION. Instead of paying the
// frontier price on every call, run a cheap first pass (mini+harness) and escalate to the frontier (gpt-5.1)
// ONLY on the tasks it fails — the reward decides. Frontier quality, a fraction of the cost. Mini and frontier
// tokens are metered separately for an exact $. The rigorous, larger version is the escbench console.
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
        double Mini((long p, long c) u) => Dollars(u, miniIn, miniOut);
        double Fr((long p, long c) u) => Dollars(u, frIn, frOut);
        int N = Suite.Length;

        if (string.IsNullOrWhiteSpace(strong))
            return new { total = N, modes = Array.Empty<object>(), frontier = false,
                headline = "Set AGENT_LLM_MODEL_STRONG (e.g. gpt-5.1) to run the escalation cost comparison." };

        // 1) always-frontier — pay gpt-5.1 on everything
        CostLedger.Reset(); int fQ = 0;
        foreach (var t in Suite) if (Hit(await ToolLoop.CompleteAsync(Sys, t.Q, 0, ct, strong), t.A)) fQ++;
        var fCost = Fr(CostLedger.Snapshot());

        // 2) cheap-first + escalate — mini+harness; on reward-fail, escalate to the frontier
        var path = Path.Combine(Path.GetTempPath(), "compare-cost-" + Guid.NewGuid().ToString("N") + ".json");
        var harness = new AgentHarness(new SemanticLessonStore(path));
        var opt = new HarnessOptions(MaxIters: 2, Threshold: 1.0, RetrieveTopK: 3, Samples: 1);
        double eMini = 0, eFr = 0; int eQ = 0, eEsc = 0;
        foreach (var t in Suite)
        {
            CostLedger.Reset();
            var o = await harness.RunAsync(new RAgent(t.Q, t.A), Ctx(t.Q), opt, ct);
            eMini += Mini(CostLedger.Snapshot());
            if (o.Best.Pass) eQ++;
            else { eEsc++; CostLedger.Reset(); var f = await ToolLoop.CompleteAsync(Sys, t.Q, 0, ct, strong); eFr += Fr(CostLedger.Snapshot()); if (Hit(f, t.A)) eQ++; }
        }
        try { File.Delete(path); } catch { /* temp */ }
        var eCost = eMini + eFr;

        var modes = new object[]
        {
            new { name = "always " + strong, quality = fQ, cost = fCost, kind = "frontier" },
            new { name = "cheap-first + escalate", quality = eQ, cost = eCost, kind = "harness" },
        };
        var pct = fCost > 0 ? eCost / fCost * 100 : 0;
        var headline = $"cheap-first + escalate reaches {eQ}/{N} (frontier: {fQ}/{N}) at ~{pct:0}% of always-frontier cost — paying the {strong} premium on only {eEsc}/{N} tasks. The reward decides when.";
        return new { total = N, modes, headline, frontier = true };
    }

    // The reasoning agent (answer-check reward + step-by-step; cheap config, Samples=1).
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
            return ToolLoop.CompleteAsync(sys, _q, 0, ct);
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
            Condition = "a word problem that hides a trap",
            Warning = "Do not answer word problems from intuition. Write the equations, solve step by step, and re-check the final number against the wording before answering.",
        };
    }
}
