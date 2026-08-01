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

// Load the local Figma target (gitignored) so the UI agent can ground generation:
//   target.png          — the design screenshot (tier 1, multimodal)
//   target-context.txt  — exact design tokens from get_design_context (tier 2, precise)
var wwwroot = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
var targetPng = Path.Combine(wwwroot, "target.png");
if (File.Exists(targetPng))
    UiTarget.ImageDataUrl = "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(targetPng));
var targetCtx = Path.Combine(wwwroot, "target-context.txt");
if (File.Exists(targetCtx))
    UiTarget.DesignContext = File.ReadAllText(targetCtx);

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

// Live cost thesis: quality + real $ for bare / mini+harness (cold, warm) / frontier on a reasoning suite.
app.MapPost("/api/cost", async (HttpRequest req) => Results.Json(await CostRun.RunAsync(req.HttpContext.RequestAborted)));

// Human-in-the-loop teaching: state a rule → stored as a Verified lesson → the agent applies it next run.
app.MapPost("/api/teach", async (HttpRequest req) =>
{
    var b = (JsonNode.Parse(await new StreamReader(req.Body).ReadToEndAsync()) as JsonObject) ?? new JsonObject();
    return Results.Json(await TeachRun.RunAsync(b["teach"]?.GetValue<string>(), b["reset"]?.GetValue<bool>() ?? false, req.HttpContext.RequestAborted));
});

// Learn-from-use: a user edits / thumbs-down / regenerates the answer → the implicit-signal adapter mints a
// Provisional lesson from that action → the next generation applies it. No teaching UI, no explicit rule.
app.MapPost("/api/implicit", async (HttpRequest req) =>
{
    var b = (JsonNode.Parse(await new StreamReader(req.Body).ReadToEndAsync()) as JsonObject) ?? new JsonObject();
    return Results.Json(await ImplicitRun.RunAsync(b, req.HttpContext.RequestAborted));
});

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
    var strong = Environment.GetEnvironmentVariable("AGENT_LLM_MODEL_STRONG");
    double PriceOf(string k, double d) => double.TryParse(Environment.GetEnvironmentVariable(k), out var v) ? v : d;
    double miniIn = PriceOf("AGENT_PRICE_IN", 0.40), miniOut = PriceOf("AGENT_PRICE_OUT", 1.60);
    double frIn = PriceOf("AGENT_PRICE_STRONG_IN", 1.25), frOut = PriceOf("AGENT_PRICE_STRONG_OUT", 10.0);
    double MiniCost() { var u = CostLedger.Snapshot(); return u.Prompt / 1e6 * miniIn + u.Completion / 1e6 * miniOut; }
    double FrCost() { var u = CostLedger.Snapshot(); return u.Prompt / 1e6 * frIn + u.Completion / 1e6 * frOut; }

    // (1) gpt-4.1-mini · NO harness — one shot, cheap.
    CostLedger.Reset();
    var mini = StripFence(await domain.BareAsync(task, ct));
    var miniCost = MiniCost();
    var miniR = domain.NewAgent(task).Evaluate(mini, ctx);

    // (2) frontier · NO harness — the SAME task on the bigger model (if configured).
    object? frontier = null;
    if (!string.IsNullOrWhiteSpace(strong))
    {
        CostLedger.Reset();
        var frAns = StripFence(await domain.BareAsync(task, ct, strong));
        var frCost = FrCost();
        var frR = domain.NewAgent(task).Evaluate(frAns, ctx);
        frontier = new { model = strong, answer = frAns, pass = frR.Pass, score = frR.Score, cost = frCost };
    }

    // (3) gpt-4.1-mini · HARNESS — the loop + persistent memory (critic is the domain's).
    var harness = new AgentHarness(store, critic: domain.Critic);
    CostLedger.Reset();
    var o = await harness.RunAsync(domain.NewAgent(task), ctx, opt, ct);
    var harnessCost = MiniCost();

    var agentId = domain.NewAgent(task).Id;
    var lessons = (await store.AllAsync()).Where(l => l.Agent == agentId)
        .Select(l => new { warning = l.Warning, trust = l.Trust.ToString() }).ToList();

    return Results.Json(new
    {
        live = Model.Enabled, model = Model.Name, frontierModel = strong, elementsTotal = domain.Elements,
        mini = new { answer = mini, pass = miniR.Pass, score = miniR.Score, cost = miniCost },
        frontier,
        harness = new
        {
            answer = StripFence(o.BestDraft ?? ""), pass = o.Best.Pass, score = o.Best.Score, cost = harnessCost,
            iterations = o.Iterations, generations = o.Generations, escalated = o.Escalated,
            injected = o.InjectedLessons, learned = o.LearnedLessons, critique = o.Best.Critique,
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
