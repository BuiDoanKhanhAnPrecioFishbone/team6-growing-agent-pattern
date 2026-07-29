using System.Text.Json.Nodes;
using AIAssistant.Agents;
using AIAssistant.Harness;

// Control panel API + static UI. Runs the S1→S6 pipeline in-process, returns each agent's result,
// and lets a human evaluate & teach (write lessons — the fast-loop "training" signal).
AIAssistant.AgentHost.Model.Configure(); // read AGENT_LLM_* — live Foundry model if set, else mock
var builder = WebApplication.CreateBuilder(args);
var storePath = Environment.GetEnvironmentVariable("FLOW_LESSON_STORE")
                ?? Path.Combine(AppContext.BaseDirectory, "ui-lessons.json");
builder.Services.AddSingleton(new SemanticLessonStore(storePath)); // Memory v2 in the UI too
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

// ── Stepwise, pause-at-each-gate execution (server-side run sessions) ──
var store = app.Services.GetRequiredService<SemanticLessonStore>();
var harness = new AgentHarness(store, () => "2026-07-27");
var opt = app.Services.GetRequiredService<HarnessOptions>();
var sessions = new System.Collections.Concurrent.ConcurrentDictionary<string, Sess>();
var gateNum = new Dictionary<string, int> { ["screen"] = 1, ["moat"] = 2, ["valuation"] = 3, ["allocation"] = 4 };
var gateAgent = new Dictionary<string, string> { ["screen"] = "s1-screen", ["moat"] = "s2-moat", ["valuation"] = "s4-valuation", ["allocation"] = "s5-allocate" };

async Task<JsonObject> RunOne(JsonObject candidate, int idx)
{
    var (key, agent, gate) = pipeline[idx];
    var ctx = new AgentContext
    {
        Ticker = candidate["ticker"]!.GetValue<string>(),
        Features = new AgentFeatures(candidate["industry"]!.GetValue<string>(), Array.Empty<string>(),
            Situation: $"{agent.Id} step for {candidate["ticker"]?.GetValue<string>()}, a {candidate["industry"]!.GetValue<string>()} company"),
        Input = candidate,
        AllowedSources = ((JsonArray)candidate["sources"]!).Select(s => s!.GetValue<string>()).ToList(),
    };
    var o = await harness.RunAsync(agent, ctx, opt, default);
    if (o.Best.Pass && o.BestDraft is not null) candidate[key] = JsonNode.Parse(o.BestDraft);
    return new JsonObject
    {
        ["id"] = agent.Id, ["key"] = key, ["gate"] = gate, ["pass"] = o.Best.Pass,
        ["iterations"] = o.Iterations, ["firstScore"] = o.FirstScore, ["score"] = o.Best.Score,
        ["injected"] = new JsonArray(o.InjectedLessons.Select(x => (JsonNode?)x.Split('|').Last()).ToArray()),
        ["learned"] = new JsonArray(o.LearnedLessons.Select(x => (JsonNode?)x.Split('|').Last()).ToArray()),
        ["critique"] = o.Best.Critique,
        ["block"] = candidate[key]?.DeepClone(),
    };
}
void ApplyGate(JsonObject c, string key)
{
    switch (key)
    {
        case "moat": c["moat"]!["humanConfirmed"] = true; break;
        case "valuation": c["valuation"]!["gate3"]!["status"] = "confirmed"; break;
        case "allocation": c["allocation"]!["humanConfirmed"] = true; break;
    }
}
JsonObject? Reco(JsonObject c)
{
    if (c["allocation"] is not JsonObject a) return null;
    var v = c["valuation"] as JsonObject; var f = c["financials"] as JsonObject; var mo = c["moat"] as JsonObject; var m = c["monitoring"] as JsonObject;
    double mid = v?["intrinsic_value_range"] is JsonObject r ? (r["low"]!.GetValue<double>() + r["high"]!.GetValue<double>()) / 2 : 0;
    return new JsonObject
    {
        ["ticker"] = c["ticker"]!.GetValue<string>(), ["name"] = c["name"]!.GetValue<string>(),
        ["grade"] = (f?["health_verdict"] as JsonObject)?["grade"]?.GetValue<string>(),
        ["moatStrength"] = mo?["moatStrength"]?.GetValue<string>(), ["moatTrend"] = mo?["moatTrend"]?.GetValue<string>(),
        ["intrinsic"] = mid, ["price"] = (v?["price"] as JsonObject)?["value"]?.GetValue<double>() ?? 0,
        ["mos"] = (v?["margin_of_safety"] as JsonObject)?["vs_mid"]?.GetValue<double>() ?? 0,
        ["decision"] = a["decision"]?.GetValue<string>(), ["size"] = a["positionSizePct"]?.GetValue<double>() ?? 0,
        ["entry"] = a["entryTarget"]?.GetValue<double>(),
        ["thesisStatus"] = m?["thesisStatus"]?.GetValue<string>(), ["alerts"] = (m?["alerts"] as JsonArray)?.Count ?? 0,
    };
}

app.MapPost("/api/run/start", (RunRequest req) =>
{
    var id = Guid.NewGuid().ToString("N")[..8];
    var c = Seed(req);
    sessions[id] = new Sess { Candidate = c };
    return Results.Json(new { runId = id, ticker = c["ticker"]!.GetValue<string>(), name = c["name"]!.GetValue<string>(), model = AIAssistant.AgentHost.Model.Name, live = AIAssistant.AgentHost.Model.Enabled, steps = pipeline.Length });
});

// Run the NEXT agent. If it has a numbered gate, the run PAUSES (PendingGate) until /api/run/gate resolves it.
app.MapPost("/api/run/step", async (StepRequest r) =>
{
    if (r.RunId is null || !sessions.TryGetValue(r.RunId, out var s)) return Results.NotFound(new { error = "unknown runId — start a run first" });
    if (s.PendingGate >= 0) return Results.Json(new { error = "resolve the pending gate first" }, statusCode: 409);
    if (s.Ran >= pipeline.Length) return Results.Json(new JsonObject { ["done"] = true, ["total"] = s.Total, ["recommendation"] = Reco(s.Candidate) });

    var idx = s.Ran;
    var stage = await RunOne(s.Candidate, idx);
    s.Total += stage["iterations"]!.GetValue<int>();
    s.Ran++;
    var key = pipeline[idx].Key;
    var pass = stage["pass"]!.GetValue<bool>();
    var blocking = pass && gateNum.ContainsKey(key);
    if (blocking) s.PendingGate = idx;
    var done = s.Ran >= pipeline.Length && s.PendingGate < 0;
    return Results.Json(new JsonObject
    {
        ["stage"] = stage,
        ["gate"] = blocking ? new JsonObject { ["n"] = gateNum[key], ["label"] = pipeline[idx].Gate, ["key"] = key, ["agent"] = gateAgent[key] } : null,
        ["failed"] = !pass,
        ["ran"] = s.Ran, ["total"] = s.Total, ["done"] = done,
        ["recommendation"] = done ? Reco(s.Candidate) : null,
    });
});

// Resolve the pending gate. confirm → apply the human decision and proceed. reject → teach a lesson and
// re-run THIS agent so you watch it improve; the gate stays pending until you confirm.
app.MapPost("/api/run/gate", async (GateRequest g) =>
{
    if (g.RunId is null || !sessions.TryGetValue(g.RunId, out var s)) return Results.NotFound(new { error = "unknown runId" });
    if (s.PendingGate < 0) return Results.BadRequest(new { error = "no pending gate" });
    var idx = s.PendingGate; var key = pipeline[idx].Key;
    if (g.Decision == "confirm")
    {
        ApplyGate(s.Candidate, key);
        await store.PromoteForAgentAsync(pipeline[idx].Agent.Id); // human confirm at the gate → promote its provisional lessons
        s.PendingGate = -1;
        return Results.Json(new { ok = true, resolved = true });
    }

    if (!string.IsNullOrWhiteSpace(g.Trigger) && !string.IsNullOrWhiteSpace(g.Warning))
        await store.WriteAsync(new Lesson
        {
            Id = $"{pipeline[idx].Agent.Id}|{s.Candidate["industry"]!.GetValue<string>()}|{g.Trigger}",
            Agent = pipeline[idx].Agent.Id, Sector = s.Candidate["industry"]!.GetValue<string>(),
            Trigger = g.Trigger!, Warning = g.Warning!, LearnedFrom = "human", Date = "2026-07-27",
        });
    var stage = await RunOne(s.Candidate, idx);
    s.Total += stage["iterations"]!.GetValue<int>();
    return Results.Json(new JsonObject { ["ok"] = true, ["resolved"] = false, ["stage"] = stage, ["total"] = s.Total });
});

// POST /api/run — one-shot run of the whole flow (gates auto-confirmed). Kept for scripting.
app.MapPost("/api/run", async (RunRequest req, SemanticLessonStore store, HarnessOptions opt) =>
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
            Features = new AgentFeatures(candidate["industry"]!.GetValue<string>(), Array.Empty<string>(),
            Situation: $"{agent.Id} step for {candidate["ticker"]?.GetValue<string>()}, a {candidate["industry"]!.GetValue<string>()} company"),
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
app.MapPost("/api/teach", async (TeachRequest t, SemanticLessonStore store) =>
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
app.MapGet("/api/lessons", async (SemanticLessonStore store) => Results.Json(await store.AllAsync()));
app.MapPost("/api/reset", (SemanticLessonStore store) => { store.Clear(); return Results.Ok(new { ok = true }); });

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
record StepRequest(string? RunId);
record GateRequest(string? RunId, string? Decision, string? Trigger, string? Warning);
sealed class Sess { public JsonObject Candidate = new(); public int Ran; public int PendingGate = -1; public int Total; }
