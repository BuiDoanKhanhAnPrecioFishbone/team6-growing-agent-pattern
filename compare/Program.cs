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

// Load the local Figma target (gitignored) so the UI agent can ground generation in the actual image.
var targetPng = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "target.png");
if (File.Exists(targetPng))
    UiTarget.ImageDataUrl = "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(targetPng));

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
    var opt = new HarnessOptions(MaxIters: domain.MaxIters, Threshold: 1.0, RetrieveTopK: 3, Samples: domain.Samples);

    // (1) bare — the playground: same task, one shot, no reward loop / memory / tools.
    var bare = StripFence(await domain.BareAsync(task, ct));
    var bareR = domain.NewAgent(task).Evaluate(bare, ctx);

    // (2) harness COLD (three-way only) — the harness's own generation with NO learned lessons, one shot.
    // Isolates the value of the MEMORY: this is what the harness produces before it has learned anything.
    object? cold = null;
    if (domain.ThreeWay)
    {
        var coldDraft = StripFence(await domain.NewAgent(task).GenerateAsync(ctx, Array.Empty<Lesson>(), null, null, 0, ct));
        var coldR = domain.NewAgent(task).Evaluate(coldDraft, ctx);
        cold = new { answer = coldDraft, pass = coldR.Pass, score = coldR.Score };
    }

    // (3) harness LEARNED — the real loop + persistent memory: recalls what it knows, learns what it missed.
    // The critic is the domain's (UI uses a vision judge that compares the output to the target image).
    var harness = new AgentHarness(store, critic: domain.Critic);
    var o = await harness.RunAsync(domain.NewAgent(task), ctx, opt, ct);

    // Everything this agent has learned so far (shown each run).
    var agentId = domain.NewAgent(task).Id;
    var lessons = (await store.AllAsync()).Where(l => l.Agent == agentId)
        .Select(l => new { warning = l.Warning, trust = l.Trust.ToString() }).ToList();

    return Results.Json(new
    {
        live = Model.Enabled, model = Model.Name,
        elementsTotal = domain.Elements,
        bare = new { answer = bare, pass = bareR.Pass, score = bareR.Score },
        cold,
        harness = new
        {
            answer = StripFence(o.BestDraft ?? ""), pass = o.Best.Pass, score = o.Best.Score,
            iterations = o.Iterations, generations = o.Generations, escalated = o.Escalated,
            injected = o.InjectedLessons, learned = o.LearnedLessons,
            critique = o.Best.Critique,
        },
        lessons,
    });
});

app.Urls.Add("http://localhost:5310");
app.Run();

// Models often wrap generated code in a ```html … ``` markdown fence — strip it so the preview renders the
// HTML itself (and the reward sees clean markup), not the literal backticks.
static string StripFence(string s)
{
    s = s.Trim();
    if (!s.StartsWith("```")) return s;
    var nl = s.IndexOf('\n');
    if (nl >= 0) s = s[(nl + 1)..];              // drop the opening ```lang line
    if (s.TrimEnd().EndsWith("```")) s = s.TrimEnd()[..^3];
    return s.Trim();
}
