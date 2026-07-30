using System.Text.Json.Nodes;
using AIAssistant.Harness;
using AIAssistant.Harness.Cosmos;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AIAssistant.AgentHost;

/// <summary>
/// Turns any <see cref="IAgent"/> into a standalone HTTP service in one line — the standard S-agent
/// surface (<c>/</c>, <c>/run</c>, <c>/lessons</c>) on the shared harness, with the lesson-store backing
/// chosen from the environment (Cosmos if <c>AGENT_COSMOS_CONNECTION</c> is set, else a local JSON file).
/// </summary>
public static class Host
{
    public static Task Run(string[] args, IAgent agent, int port, string blockKey)
    {
        Model.Configure(); // read AGENT_LLM_* — live model if set, else the deterministic mock

        // Amplifier levers, both opt-in and inert without a live model:
        //   AGENT_SELF_VERIFY=1        → an LLM critic reviews each draft (self-verify)
        //   AGENT_LLM_MODEL_STRONG=... → escalate hard cases (below threshold) to that bigger deployment
        var selfVerify = (Environment.GetEnvironmentVariable("AGENT_SELF_VERIFY") ?? "").ToLowerInvariant() is "1" or "true" or "on" or "yes";
        EscalateDraft? escalate = Model.StrongConfigured
            ? async (a, ctx, lessons, critique, ct) => await Model.WithStrongModel(() => a.GenerateAsync(ctx, lessons, critique, null, 0, ct))
            : null;

        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddSingleton(HarnessOptions.FromEnvironment());
        builder.Services.AddSingleton<ILessonStore>(_ => StoreFromEnv());
        builder.Services.AddSingleton(sp => new AgentHarness(
            sp.GetRequiredService<ILessonStore>(),
            critic: selfVerify ? new LlmCritic() : null,
            escalate: escalate));
        var app = builder.Build();

        app.MapGet("/", () => Results.Ok(new
        {
            service = agent.Id, block = blockKey, status = "up",
            model = Model.Name, live = Model.Enabled,
            selfVerify, strongModel = Model.StrongName,
        }));

        // POST /run — body is the candidate file. Runs the fast loop for THIS agent, merges its block
        // back in, and returns the candidate plus this agent's loop telemetry under `agent`.
        app.MapPost("/run", async (HttpRequest request, AgentHarness harness, HarnessOptions opt) =>
        {
            var candidate = await ReadObject(request);
            var ctx = new AgentContext
            {
                Ticker = candidate["ticker"]?.GetValue<string>() ?? "UNKNOWN",
                Features = new AgentFeatures(candidate["industry"]?.GetValue<string>() ?? "general", Array.Empty<string>(),
                    Situation: $"{agent.Id} step for {candidate["ticker"]?.GetValue<string>()}, a {candidate["industry"]?.GetValue<string>()} company"),
                Input = candidate,
                AllowedSources = (candidate["sources"] as JsonArray ?? new JsonArray())
                    .Select(s => s?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToList(),
            };

            var o = await harness.RunAsync(agent, ctx, opt, request.HttpContext.RequestAborted);
            candidate[blockKey] = o.Best.Pass && o.BestDraft is not null ? JsonNode.Parse(o.BestDraft) : null;
            candidate["agent"] = new JsonObject
            {
                ["id"] = agent.Id,
                ["pass"] = o.Best.Pass,
                ["iterations"] = o.Iterations,
                ["firstScore"] = o.FirstScore,
                ["score"] = o.Best.Score,
                ["injectedLessons"] = new JsonArray(o.InjectedLessons.Select(x => (JsonNode?)x).ToArray()),
                ["learnedLessons"] = new JsonArray(o.LearnedLessons.Select(x => (JsonNode?)x).ToArray()),
                ["generations"] = o.Generations,
                ["escalated"] = o.Escalated,
                ["critique"] = o.Best.Critique,
            };
            return Results.Json(candidate);
        });

        app.MapGet("/lessons", async (ILessonStore store) => Results.Json(await store.AllAsync()));

        if (port > 0) app.Urls.Add($"http://localhost:{port}");
        return app.RunAsync();
    }

    private static ILessonStore StoreFromEnv()
    {
        var conn = Environment.GetEnvironmentVariable("AGENT_COSMOS_CONNECTION");
        if (!string.IsNullOrWhiteSpace(conn))
        {
            var db = Environment.GetEnvironmentVariable("AGENT_COSMOS_DB") ?? "team6";
            var cont = Environment.GetEnvironmentVariable("AGENT_COSMOS_CONTAINER") ?? "lessons";
            // AGENT_COSMOS_VECTOR=1 → server-side vector search (needs the account's NoSQL vector-search
            // capability + AGENT_EMBED_*; see docs/COSMOS-MEMORY.md). Otherwise the exact-match backing.
            var vector = (Environment.GetEnvironmentVariable("AGENT_COSMOS_VECTOR") ?? "").ToLowerInvariant() is "1" or "true" or "on";
            return vector ? new CosmosSemanticLessonStore(conn, db, cont) : new CosmosLessonStore(conn, db, cont);
        }
        // Memory v2: semantic retrieval (embeddings + LLM recall) behind the same ILessonStore seam;
        // empty situation → v1 hit-rate ordering, so it stays drop-in.
        return new SemanticLessonStore(Environment.GetEnvironmentVariable("AGENT_LESSON_STORE")
                                       ?? Path.Combine(AppContext.BaseDirectory, "lessons.json"));
    }

    private static async Task<JsonObject> ReadObject(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body);
        var text = await reader.ReadToEndAsync();
        return (JsonNode.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text) as JsonObject) ?? new JsonObject();
    }
}
