using System.Text.Json.Nodes;
using AIAssistant.Agent;
using AIAssistant.Domain;
using AIAssistant.Harness;
using AIAssistant.Harness.Cosmos;

// S2 · Moat — a standalone HTTP agent built on the reusable Agent Harness. Same shape as S3:
// POST /run takes a candidate-file fragment, runs the fast loop (generate → evaluate → retrieve
// lesson → revise → pick best → write lesson), and returns the drafted `moat` block + loop telemetry.
// Runs offline (deterministic mock model) until S2_LLM_BASE_URL points it at an Azure AI Foundry model.
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient<ChatClient>(c => c.Timeout = TimeSpan.FromSeconds(120));
builder.Services.AddSingleton(S2AgentOptions.FromEnvironment());
builder.Services.AddSingleton(HarnessOptions.FromEnvironment());
// Memory backing chosen by environment: Cosmos (partition /agent) in the cloud, JSON file for the
// offline demo. Same ILessonStore contract either way — the loop and the agent never know which.
builder.Services.AddSingleton<ILessonStore>(_ =>
{
    var conn = Environment.GetEnvironmentVariable("S2_COSMOS_CONNECTION")
               ?? Environment.GetEnvironmentVariable("AGENT_COSMOS_CONNECTION");
    if (!string.IsNullOrWhiteSpace(conn))
        return new CosmosLessonStore(conn,
            Environment.GetEnvironmentVariable("AGENT_COSMOS_DB") ?? "team6",
            Environment.GetEnvironmentVariable("AGENT_COSMOS_CONTAINER") ?? "lessons");

    return new JsonLessonStore(Environment.GetEnvironmentVariable("S2_LESSON_STORE")
                               ?? Path.Combine(AppContext.BaseDirectory, "lessons.json"));
});
builder.Services.AddSingleton<AgentHarness>();
builder.Services.AddScoped<MoatAgentFactory>();
var app = builder.Build();

app.MapGet("/", () => Results.Ok(new { service = "s2-moat", status = "up" }));

app.MapGet("/schema", () => Results.Json(new
{
    name = "s2-moat",
    title = "S2 · Moat",
    consumes = new[] { "ticker", "name", "sector", "screen", "sources" },
    produces = new[] { "moat", "agent", "gate" },
    note = "Qualitative agent on the Agent Harness. `sources` are the citations it is allowed to use " +
           "(the environment); the reward drops any invented citation. `moat` is a DRAFT until Gate #2.",
}));

// POST /run — body: { ticker, name?, sector?, screen?, sources?: [{id?,title}] } (or { input: {...} }).
// Returns the input merged with { moat, agent, gate }. moat is null when no draft passed the hard gates.
app.MapPost("/run", async (HttpRequest request, MoatAgentFactory factory, AgentHarness harness, HarnessOptions opt, S2AgentOptions llm) =>
{
    var root = await ReadRoot(request);
    var input = root["input"] as JsonObject ?? root;

    var ticker = input["ticker"]?.GetValue<string>();
    if (string.IsNullOrWhiteSpace(ticker))
        return Results.BadRequest(new { error = "s2-moat requires 'ticker' in the body" });

    var ctx = BuildContext(input, ticker);
    var outcome = await harness.RunAsync(factory.Create(), ctx, opt, request.HttpContext.RequestAborted);

    input["moat"] = outcome.Best.Pass && outcome.BestDraft is not null
        ? JsonNode.Parse(outcome.BestDraft)
        : null;
    input["agent"] = new JsonObject
    {
        ["llmEnabled"] = llm.Enabled,
        ["model"] = llm.Enabled ? llm.Model : "mock (offline — set S2_LLM_BASE_URL to use Foundry)",
        ["iterations"] = outcome.Iterations,
        ["score"] = outcome.Best.Score,
        ["firstScore"] = outcome.FirstScore,
        ["pass"] = outcome.Best.Pass,
        ["scoreBreakdown"] = ToObject(outcome.Best.Breakdown),
        ["injectedLessons"] = new JsonArray(outcome.InjectedLessons.Select(x => (JsonNode?)x).ToArray()),
        ["learnedLessons"] = new JsonArray(outcome.LearnedLessons.Select(x => (JsonNode?)x).ToArray()),
        ["critique"] = outcome.Best.Critique,
    };
    input["gate"] = outcome.Best.Pass
        ? new JsonObject
        {
            ["id"] = 2,
            ["prompt"] = $"{ticker} — moat draft ready. Confirm strength/trend, or downgrade? Toggle circle-of-competence?",
            ["onConfirm"] = "set moat.humanConfirmed = true (or apply edits), then advance to S3",
        }
        : null;

    return Results.Json(input, S2Json.Options);
});

// POST /score — the reward in isolation. Body { draft, ticker, sector?, sources? }. Used by the trainer/debuggers.
app.MapPost("/score", async (HttpRequest request, MoatAgentFactory factory) =>
{
    var root = await ReadRoot(request);
    var input = root["input"] as JsonObject ?? root;
    var draft = input["draft"]?.ToJsonString() ?? input["draft"]?.GetValue<string>();
    if (draft is null)
        return Results.BadRequest(new { error = "'draft' (the moat JSON to score) is required" });

    var ctx = BuildContext(input, input["ticker"]?.GetValue<string>() ?? "UNKNOWN");
    var r = factory.Create().Evaluate(draft, ctx);
    return Results.Json(new { r.Pass, r.Score, r.Breakdown, failedTriggers = r.FailedTriggers, r.Critique }, S2Json.Options);
});

// GET /lessons — inspect the memory (what the agent has learned, with hit-rates).
app.MapGet("/lessons", async (ILessonStore store) => Results.Json(await store.AllAsync(), S2Json.Pretty));

app.Run();

static async Task<JsonObject> ReadRoot(HttpRequest request)
{
    using var reader = new StreamReader(request.Body);
    var text = await reader.ReadToEndAsync();
    return (string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text)) as JsonObject ?? new JsonObject();
}

static AgentContext BuildContext(JsonObject input, string ticker)
{
    var sector = input["sector"]?.GetValue<string>() ?? "general";
    var sources = (input["sources"] as JsonArray ?? new JsonArray())
        .Select(s => s is JsonObject o
            ? string.Join(" ", new[] { o["id"]?.GetValue<string>(), o["title"]?.GetValue<string>() }.Where(x => !string.IsNullOrWhiteSpace(x)))
            : s?.GetValue<string>() ?? "")
        .Where(s => s.Length > 0)
        .ToList();

    return new AgentContext
    {
        Ticker = ticker,
        Features = new AgentFeatures(sector, Tags: Array.Empty<string>()),
        Input = input,
        AllowedSources = sources,
    };
}

static JsonObject ToObject(IReadOnlyDictionary<string, double> map)
{
    var obj = new JsonObject();
    foreach (var (k, v) in map) obj[k] = v;
    return obj;
}

// A tiny factory so each request gets a MoatAgent bound to the shared ChatClient.
public sealed class MoatAgentFactory
{
    private readonly ChatClient _chat;
    public MoatAgentFactory(ChatClient chat) => _chat = chat;
    public MoatAgent Create() => new(_chat);
}
