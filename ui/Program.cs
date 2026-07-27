using System.Text.Json.Nodes;
using AIAssistant.Agents;
using AIAssistant.Harness;

// Control panel API + static UI. Runs the S1→S6 pipeline in-process, returns each agent's result,
// and lets a human evaluate & teach (write lessons — the fast-loop "training" signal).
AIAssistant.AgentHost.Model.Configure(); // read AGENT_LLM_* — live Foundry model if set, else mock
var builder = WebApplication.CreateBuilder(args);
var storePath = Environment.GetEnvironmentVariable("FLOW_LESSON_STORE")
                ?? Path.Combine(AppContext.BaseDirectory, "ui-lessons.json");
builder.Services.AddSingleton(new JsonLessonStore(storePath));
builder.Services.AddSingleton(HarnessOptions.FromEnvironment());
var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

var pipeline = new (string Key, IAgent Agent, string? Gate)[]
{
    ("screen",     new ScreenAgent(),     "confirm shortlist"),
    ("moat",       new MoatAgent(),       "confirm moat strength"),
    ("financials", new FinancialsAgent(), null),
    ("valuation",  new ValuationAgent(),  "confirm assumptions"),
    ("allocation", new AllocateAgent(),   "approve buy / size"),
    ("monitoring", new MonitorAgent(),    "act on alerts"),
};

// POST /api/run — run the whole flow for one company; return per-agent results + recommendation.
app.MapPost("/api/run", async (RunRequest req, JsonLessonStore store, HarnessOptions opt) =>
{
    var harness = new AgentHarness(store, () => "2026-07-27");
    var candidate = Seed(req);
    var stages = new JsonArray();
    var total = 0;

    foreach (var (key, agent, gate) in pipeline)
    {
        var ctx = new AgentContext
        {
            Ticker = candidate["ticker"]!.GetValue<string>(),
            Features = new AgentFeatures(candidate["industry"]!.GetValue<string>(), Array.Empty<string>()),
            Input = candidate,
            AllowedSources = ((JsonArray)candidate["sources"]!).Select(s => s!.GetValue<string>()).ToList(),
        };
        var o = await harness.RunAsync(agent, ctx, opt, default);
        total += o.Iterations;
        if (o.Best.Pass && o.BestDraft is not null) candidate[key] = JsonNode.Parse(o.BestDraft);

        stages.Add(new JsonObject
        {
            ["id"] = agent.Id, ["key"] = key, ["gate"] = gate,
            ["pass"] = o.Best.Pass, ["iterations"] = o.Iterations,
            ["firstScore"] = o.FirstScore, ["score"] = o.Best.Score,
            ["injected"] = new JsonArray(o.InjectedLessons.Select(x => (JsonNode?)Trigger(x)).ToArray()),
            ["learned"] = new JsonArray(o.LearnedLessons.Select(x => (JsonNode?)Trigger(x)).ToArray()),
            ["critique"] = o.Best.Critique,
            ["block"] = candidate[key]?.DeepClone(),
        });

        if (gate is not null && o.Best.Pass)
            switch (key)
            {
                case "moat": candidate["moat"]!["humanConfirmed"] = true; break;
                case "valuation": candidate["valuation"]!["gate3"]!["status"] = "confirmed"; break;
                case "allocation": candidate["allocation"]!["humanConfirmed"] = true; break;
            }
    }

    return Results.Json(new JsonObject
    {
        ["ticker"] = candidate["ticker"]!.GetValue<string>(),
        ["name"] = candidate["name"]!.GetValue<string>(),
        ["model"] = AIAssistant.AgentHost.Model.Name,
        ["live"] = AIAssistant.AgentHost.Model.Enabled,
        ["totalIterations"] = total,
        ["stages"] = stages,
        ["candidate"] = candidate.DeepClone(),
    });
});

// POST /api/teach — a human evaluation becomes a lesson the agent applies next run. This is "training"
// in the Foundry-only sense (context, not weights) — and the same records become the ART corpus later.
app.MapPost("/api/teach", async (TeachRequest t, JsonLessonStore store) =>
{
    if (string.IsNullOrWhiteSpace(t.Agent) || string.IsNullOrWhiteSpace(t.Trigger) || string.IsNullOrWhiteSpace(t.Warning))
        return Results.BadRequest(new { error = "agent, trigger and warning are required" });
    var sector = string.IsNullOrWhiteSpace(t.Sector) ? "consumer_staples" : t.Sector!;
    await store.WriteAsync(new Lesson
    {
        Id = $"{t.Agent}|{sector}|{t.Trigger}", Agent = t.Agent!, Sector = sector,
        Trigger = t.Trigger!, Warning = t.Warning!, LearnedFrom = "human", Date = "2026-07-27",
    });
    return Results.Ok(new { ok = true });
});

app.MapGet("/api/status", () => Results.Json(new { model = AIAssistant.AgentHost.Model.Name, live = AIAssistant.AgentHost.Model.Enabled }));
app.MapGet("/api/lessons", async (JsonLessonStore store) => Results.Json(await store.AllAsync()));
app.MapPost("/api/reset", (JsonLessonStore store) => { store.Clear(); return Results.Ok(new { ok = true }); });

if (!args.Any(a => a.StartsWith("--urls")) && Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is null)
    app.Urls.Add("http://localhost:5300");
app.Run();

static string Trigger(string lessonId) => lessonId.Split('|').Last();

static JsonObject Seed(RunRequest r)
{
    var sources = r.Sources is { Length: > 0 } ? r.Sources
        : new[] { $"{r.Ticker} Annual Report 2025 p.12", $"{r.Ticker} Annual Report 2025 p.28", "vnstock:ratios 2020-2025", "BS2026Q1" };
    return new JsonObject
    {
        ["contractVersion"] = "2.0",
        ["ticker"] = string.IsNullOrWhiteSpace(r.Ticker) ? "VNM" : r.Ticker,
        ["name"] = string.IsNullOrWhiteSpace(r.Name) ? "Sample Company" : r.Name,
        ["exchange"] = "HOSE",
        ["industry"] = string.IsNullOrWhiteSpace(r.Industry) ? "consumer_staples" : r.Industry,
        ["asOf"] = "2026-07-07",
        ["statements"] = new JsonObject { ["meta"] = new JsonObject { ["shares_out_millions"] = 2090, ["price_latest"] = new JsonObject { ["value"] = 18100, ["unit"] = "VND/share", ["as_of"] = "2026-07-01" } } },
        ["sources"] = new JsonArray(sources.Select(s => (JsonNode?)s).ToArray()),
        ["screen"] = null, ["moat"] = null, ["financials"] = null, ["valuation"] = null, ["allocation"] = null, ["monitoring"] = null,
        ["provenance"] = new JsonArray(),
    };
}

record RunRequest(string? Ticker, string? Name, string? Industry, string[]? Sources);
record TeachRequest(string? Agent, string? Sector, string? Trigger, string? Warning);
