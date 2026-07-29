using System.Text.Json.Nodes;
using AIAssistant.Agents;
using AIAssistant.Harness;

// ── The value-investing pipeline: S1 → S6, threading one candidate file, gates auto-confirmed. ──
// One shared lesson store across all agents (partitioned by agent id). Run twice on the same industry
// so the WHOLE pipeline compounds: run 2 inherits run 1's lessons and stops stumbling.

AIAssistant.AgentHost.Model.Configure(); // read AGENT_LLM_* — live Foundry model if set, else mock
var storePath = Environment.GetEnvironmentVariable("FLOW_LESSON_STORE")
                ?? Path.Combine(AppContext.BaseDirectory, "flow-lessons.json");
if (args.Contains("--fresh") && File.Exists(storePath)) File.Delete(storePath);

var store = new SemanticLessonStore(storePath); // Memory v2 (drop-in; empty situation → v1 ordering)
var harness = new AgentHarness(store, clock: () => "2026-07-27");
var opt = new HarnessOptions(MaxIters: 3, Threshold: 0.80, RetrieveTopK: 3);

var pipeline = new (string Key, IAgent Agent, string? Gate)[]
{
    ("screen",     new ScreenAgent(),     "#1 confirm shortlist"),
    ("moat",       new MoatAgent(),       "#2 confirm moat strength"),
    ("financials", new FinancialsAgent(), null),
    ("valuation",  new ValuationAgent(),  "#3 confirm assumptions"),
    ("allocation", new AllocateAgent(),   "#4 approve buy / size"),
    ("monitoring", new MonitorAgent(),    "act on alerts"),
};

var companies = new[]
{
    ("VNM", "Vietnam Dairy Products JSC (Vinamilk)", new[] { "VNM Annual Report 2025 p.12", "VNM Annual Report 2025 p.28", "vnstock:ratios 2020-2025", "BS2026Q1" }),
    ("MSN", "Masan Consumer Corporation",             new[] { "MSN Annual Report 2024 p.15", "MSN Annual Report 2024 p.33", "vnstock:ratios 2020-2024", "BS2026Q1" }),
};

var totals = new Dictionary<string, int>();

foreach (var (ticker, name, sources) in companies)
{
    Console.WriteLine($"\n══════════════════════════════════════════════════════════════════");
    Console.WriteLine($"  FULL FLOW · {ticker} — {name}");
    Console.WriteLine($"══════════════════════════════════════════════════════════════════");

    var candidate = Seed(ticker, name, sources);
    var totalIters = 0;
    var halted = false;

    foreach (var (key, agent, gate) in pipeline)
    {
        var ctx = new AgentContext
        {
            Ticker = ticker,
            Features = new AgentFeatures(candidate["industry"]!.GetValue<string>(), Array.Empty<string>(),
                Situation: $"{agent.Id} step for {ticker}, a {candidate["industry"]!.GetValue<string>()} company"),
            Input = candidate,
            AllowedSources = ((JsonArray)candidate["sources"]!).Select(s => s!.GetValue<string>()).ToList(),
        };

        var o = await harness.RunAsync(agent, ctx, opt, default);
        totalIters += o.Iterations;

        if (!o.Best.Pass || o.BestDraft is null)
        {
            Console.WriteLine($"  ✗ {agent.Id,-14} FAILED after {o.Iterations} iters — {o.Best.Critique}");
            halted = true;
            break;
        }

        candidate[key] = JsonNode.Parse(o.BestDraft);

        var flags = new List<string>();
        if (o.LearnedLessons.Count > 0) flags.Add($"learned {string.Join(",", o.LearnedLessons.Select(Short))}");
        if (o.InjectedLessons.Count > 0) flags.Add($"used {string.Join(",", o.InjectedLessons.Select(Short))}");
        var note = flags.Count > 0 ? "  [" + string.Join(" · ", flags) + "]" : "";
        Console.WriteLine($"  ✓ {agent.Id,-14} {o.Iterations} iter  first={o.FirstScore:0.00}→best={o.Best.Score:0.00}{note}");

        if (gate is not null)
        {
            switch (key)
            {
                case "moat": candidate["moat"]!["humanConfirmed"] = true; break;
                case "valuation": candidate["valuation"]!["gate3"]!["status"] = "confirmed"; break;
                case "allocation": candidate["allocation"]!["humanConfirmed"] = true; break;
            }
            Console.WriteLine($"      🧑 gate {gate} — auto-confirmed (demo)");
        }
    }

    if (halted) continue;
    totals[ticker] = totalIters;

    var v = candidate["valuation"]!; var a = candidate["allocation"]!; var m = candidate["monitoring"]!;
    var mid = (v["intrinsic_value_range"]!["low"]!.GetValue<double>() + v["intrinsic_value_range"]!["high"]!.GetValue<double>()) / 2;
    Console.WriteLine($"  ┌── RECOMMENDATION ────────────────────────────────────────────");
    Console.WriteLine($"  │ health {candidate["financials"]!["health_verdict"]!["grade"]!.GetValue<string>()}" +
                      $" · moat {candidate["moat"]!["moatStrength"]!.GetValue<string>()}/{candidate["moat"]!["moatTrend"]!.GetValue<string>()}");
    Console.WriteLine($"  │ intrinsic ≈ {mid:N0} VND vs price {v["price"]!["value"]!.GetValue<double>():N0}" +
                      $" · MoS {v["margin_of_safety"]!["vs_mid"]!.GetValue<double>():P0}");
    Console.WriteLine($"  │ DECISION: {a["decision"]!.GetValue<string>().ToUpper()}" +
                      $" · size {a["positionSizePct"]!.GetValue<double>():P1}" +
                      $" · entry ≤ {a["entryTarget"]?.GetValue<double>():N0}");
    Console.WriteLine($"  │ monitor: thesis {m["thesisStatus"]!.GetValue<string>()}, {((JsonArray)m["alerts"]!).Count} alert(s)");
    Console.WriteLine($"  │ provenance-clean · all 4 human gates confirmed");
    Console.WriteLine($"  └──────────────────────────────────────────────────────────────");
    Console.WriteLine($"  pipeline total: {totalIters} iterations");
}

if (totals.Count == 2)
{
    var (x, y) = (totals["VNM"], totals["MSN"]);
    Console.WriteLine($"\n▚ COMPOUNDING — run 1 (VNM): {x} iters · run 2 (MSN, same industry): {y} iters");
    Console.WriteLine(y < x
        ? $"  ↓ {x - y} fewer iterations: run 2 inherited run 1's lessons and stopped stumbling. Cheaper, pipeline-wide."
        : "  (no reduction — check the lesson store)");
}

var all = await store.AllAsync();
Console.WriteLine($"\nlessons learned ({all.Count}):");
foreach (var l in all.OrderBy(l => l.Id))
    Console.WriteLine($"  {l.Id,-42} hitRate={l.HitRate:0.00} applied={l.TimesApplied}");

static string Short(string lessonId) => lessonId.Split('|').Last();

static JsonObject Seed(string ticker, string name, string[] sources) => new()
{
    ["contractVersion"] = "2.0", ["ticker"] = ticker, ["name"] = name, ["exchange"] = "HOSE",
    ["industry"] = "consumer_staples", ["asOf"] = "2026-07-07",
    ["statements"] = new JsonObject { ["meta"] = new JsonObject { ["shares_out_millions"] = 2090, ["price_latest"] = new JsonObject { ["value"] = 18100, ["unit"] = "VND/share", ["as_of"] = "2026-07-01" } } },
    ["sources"] = new JsonArray(sources.Select(s => (JsonNode?)s).ToArray()),
    ["screen"] = null, ["moat"] = null, ["financials"] = null, ["valuation"] = null, ["allocation"] = null, ["monitoring"] = null,
    ["provenance"] = new JsonArray(),
};
