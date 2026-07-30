using System.Text.Json.Nodes;
using AIAssistant.Harness;
using AIAssistant.AgentHost;
using Compare;

// Harness-vs-playground comparison: same cheap model, one side bare (a single completion, like the Foundry
// playground), the other run through the real AgentHarness + a persistent lesson memory. Run again to watch
// the harness side compound while the bare side stays flat.

Model.Configure();
var storePath = Path.Combine(AppContext.BaseDirectory, "compare-lessons.json");
var store = new SemanticLessonStore(storePath);

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/domains", () => Results.Json(new
{
    live = Model.Enabled,
    model = Model.Name,
    domains = Registry.All.Select(d => new
    {
        key = d.Key, title = d.Title, blurb = d.Blurb,
        tasks = d.Tasks.Select(t => new { prompt = t.Prompt, note = t.Note, sources = t.Sources }),
    }),
}));

app.MapPost("/api/reset", () => { store.Clear(); return Results.Json(new { ok = true }); });

app.MapPost("/api/compare", async (HttpRequest req) =>
{
    var body = (JsonNode.Parse(await new StreamReader(req.Body).ReadToEndAsync()) as JsonObject) ?? new JsonObject();
    var domain = Registry.Get(body["domain"]?.GetValue<string>() ?? "");
    if (domain is null) return Results.BadRequest(new { error = "unknown domain" });
    var idx = body["task"]?.GetValue<int>() ?? 0;
    if (idx < 0 || idx >= domain.Tasks.Count) idx = 0;
    var task = domain.Tasks[idx];
    var ct = req.HttpContext.RequestAborted;

    var ctx = new AgentContext
    {
        Ticker = domain.Key,
        Features = new AgentFeatures(domain.Sector, Array.Empty<string>(), $"{domain.Title}: {task.Prompt}"),
        Input = new JsonObject { ["task"] = task.Prompt },
        AllowedSources = task.Sources,
    };
    var opt = new HarnessOptions(MaxIters: 3, Threshold: 1.0, RetrieveTopK: 3, Samples: domain.Samples);

    // (1) bare — the playground: same task, one shot, no reward loop / memory / tools.
    var bare = await domain.BareAsync(task, ct);
    var bareR = domain.NewAgent(task).Evaluate(bare, ctx);

    // (2) harness — the real loop + the persistent lesson memory (shared across runs → it compounds).
    var harness = new AgentHarness(store, critic: domain.SelfVerify ? new LlmCritic() : null);
    var o = await harness.RunAsync(domain.NewAgent(task), ctx, opt, ct);

    return Results.Json(new
    {
        live = Model.Enabled, model = Model.Name,
        bare = new { answer = bare, pass = bareR.Pass, score = bareR.Score },
        harness = new
        {
            answer = o.BestDraft ?? "", pass = o.Best.Pass, score = o.Best.Score,
            iterations = o.Iterations, generations = o.Generations, escalated = o.Escalated,
            injected = o.InjectedLessons, learned = o.LearnedLessons,
            critique = o.Best.Critique,
        },
    });
});

app.Urls.Add("http://localhost:5310");
app.Run();
