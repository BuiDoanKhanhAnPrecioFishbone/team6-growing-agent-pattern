using System.Text.Json.Nodes;
using AIAssistant.AgentHost;
using AIAssistant.Harness;

namespace AIAssistant.Agents;

/// <summary>S3 · Financials — mechanical health verdict. Learnable flaw: omitting a fired red flag.</summary>
public sealed class FinancialsAgent : IAgent
{
    public string Id => "s3-financials";

    public Task<string> GenerateAsync(AgentContext ctx, IReadOnlyList<Lesson> lessons, string? critique, string? prior, int attempt, CancellationToken ct)
    {
        var block = new JsonObject
        {
            ["sections"] = new JsonObject
            {
                ["s3-dupont"] = new JsonObject { ["debt_to_equity"] = 0.127, ["roe"] = 0.291 },
                ["s3-normalize"] = new JsonObject { ["fcf"] = 8600, ["owner_earnings"] = 11050 },
            },
            ["health_verdict"] = new JsonObject { ["grade"] = "A", ["trend"] = 3, ["quality"] = 3, ["balance_sheet"] = 3, ["dilution"] = 1, ["total"] = 10 },
            ["red_flags"] = Kit.Corrected(lessons, critique, "MISSING_REDFLAG")
                ? new JsonArray(new JsonObject { ["id"] = "TURNOVER_DECLINE", ["rule"] = "asset turnover down ≥3 consecutive years", ["evidence"] = new JsonArray("total_assets.2022-2025") })
                : new JsonArray(),
            ["sanity_flags"] = new JsonArray(),
        };
        return Task.FromResult(block.ToJsonString(Kit.J));
    }

    public Reward Evaluate(string draft, AgentContext ctx)
    {
        var b = Kit.Obj(draft);
        var v = b["health_verdict"] as JsonObject;
        if (v?["grade"]?.GetValue<string>() is not { Length: 1 } grade || !"ABCDF".Contains(grade))
            return Kit.Fail("SCHEMA", "GATE schema: financials needs health_verdict with an A–F grade.", "verdictBacked", "flagCoverage");
        if (b["red_flags"] is not JsonArray rf || rf.Count == 0)
            return Kit.Fail("MISSING_REDFLAG", "GATE coverage: surface the fired red flag(s) — the data shows a multi-year turnover decline.", "verdictBacked", "flagCoverage");
        return Kit.Pass($"Financials verdict {grade} — grounded and complete.", new() { ["verdictBacked"] = 1, ["flagCoverage"] = 1 });
    }

    public Lesson? LessonFor(string trigger, AgentContext ctx) => trigger == "MISSING_REDFLAG"
        ? Kit.Lesson(Id, ctx, trigger, $"For {ctx.Features.Sector}, always surface every fired red flag (e.g. TURNOVER_DECLINE) — never emit an empty red_flags when a rule fired.")
        : null;
}
