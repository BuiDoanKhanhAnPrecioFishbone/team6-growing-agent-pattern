using System.Text.Json.Nodes;
using AIAssistant.Harness;
using AIAssistant.STemplate;

// A growing agent, in ~30 lines of wiring. The loop, memory and reward-contract come from the
// shared harness; this file just exposes the agent over HTTP with the standard S-agent surface.
// Copy this folder to agents/sN, rename, and implement the three TODOs in TemplateAgent.cs.
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(HarnessOptions.FromEnvironment());
// Memory: JSON file for the demo. To go Azure-native, add a ProjectReference to
// AIAssistant.AgentHarness.Cosmos and return `new CosmosLessonStore(conn)` when a connection string is
// set — same ILessonStore contract, no other change (see agents/s2/Program.cs for the selector).
builder.Services.AddSingleton<ILessonStore>(_ =>
    new JsonLessonStore(Environment.GetEnvironmentVariable("AGENT_LESSON_STORE")
                        ?? Path.Combine(AppContext.BaseDirectory, "lessons.json")));
builder.Services.AddSingleton<AgentHarness>();
var app = builder.Build();

app.MapGet("/", () => Results.Ok(new { service = "sX-template", status = "up" }));

// POST /run — body: { ticker, sector?, sources?: [string|{title}], ... }. Returns { draft, agent, gate }.
app.MapPost("/run", async (HttpRequest request, AgentHarness harness, HarnessOptions opt) =>
{
    var input = await ReadRoot(request);
    var ticker = input["ticker"]?.GetValue<string>();
    if (string.IsNullOrWhiteSpace(ticker))
        return Results.BadRequest(new { error = "requires 'ticker'" });

    var ctx = new AgentContext
    {
        Ticker = ticker,
        Features = new AgentFeatures(input["sector"]?.GetValue<string>() ?? "general", Array.Empty<string>()),
        Input = input,
        AllowedSources = (input["sources"] as JsonArray ?? new JsonArray())
            .Select(s => s is JsonObject o ? o["title"]?.GetValue<string>() ?? "" : s?.GetValue<string>() ?? "")
            .Where(s => s.Length > 0).ToList(),
    };

    var outcome = await harness.RunAsync(new TemplateAgent(), ctx, opt, request.HttpContext.RequestAborted);

    return Results.Json(new
    {
        draft = outcome.Best.Pass && outcome.BestDraft is not null ? JsonNode.Parse(outcome.BestDraft) : null,
        agent = new
        {
            outcome.Iterations,
            outcome.Best.Score,
            outcome.FirstScore,
            outcome.Best.Pass,
            outcome.Best.Breakdown,
            failedTriggers = outcome.Best.FailedTriggers,
            outcome.InjectedLessons,
            outcome.LearnedLessons,
            outcome.Best.Critique,
        },
    });
});

// GET /lessons — inspect what the agent has learned (with hit-rates).
app.MapGet("/lessons", async (ILessonStore store) => Results.Json(await store.AllAsync()));

app.Run();

static async Task<JsonObject> ReadRoot(HttpRequest request)
{
    using var reader = new StreamReader(request.Body);
    var text = await reader.ReadToEndAsync();
    return (JsonNode.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text) as JsonObject) ?? new JsonObject();
}
