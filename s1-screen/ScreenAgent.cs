using System.Text.Json.Nodes;
using AIAssistant.AgentHost;
using AIAssistant.Harness;

namespace AIAssistant.Agents;

/// <summary>S1 · Screen — quantitative shortlist. Learnable flaw: forgetting to echo the enforced criteria.</summary>
public sealed class ScreenAgent : IAgent
{
    public string Id => "s1-screen";

    public Task<string> GenerateAsync(AgentContext ctx, IReadOnlyList<Lesson> lessons, string? critique, string? prior, int attempt, CancellationToken ct)
    {
        var block = new JsonObject
        {
            ["passed"] = true,
            ["rank"] = 1,
            ["universe"] = new JsonObject { ["resolvedNote"] = "market=[HOSE,HNX] & index=VN30: 8 tickers", ["symbolsMode"] = false },
            ["metrics"] = new JsonObject
            {
                ["roe5y"] = new JsonArray(0.318, 0.296, 0.262, 0.282, 0.291),
                ["netMarginTrend"] = "stable", ["deToEquity"] = 0.127, ["fcfPositiveYears"] = 5, ["epsGrowth"] = 0.051,
            },
        };
        if (Kit.Corrected(lessons, critique, "MISSING_CRITERIA"))
            block["criteriaUsed"] = new JsonObject
            {
                ["roe5yMin"] = 0.15, ["netMarginTrend"] = "stable_or_rising",
                ["deToEquityMax"] = 1.0, ["fcfPositiveYears"] = 4, ["epsGrowthMin"] = 0.05,
            };
        return Task.FromResult(block.ToJsonString(Kit.J));
    }

    public Reward Evaluate(string draft, AgentContext ctx)
    {
        var b = Kit.Obj(draft);
        if (b["passed"] is null || b["metrics"] is null)
            return Kit.Fail("SCHEMA", "GATE schema: screen needs passed + metrics.", "completeness", "provenance");
        if (b["criteriaUsed"] is null)
            return Kit.Fail("MISSING_CRITERIA", "GATE provenance: echo criteriaUsed — the exact thresholds enforced — so rank is auditable.", "completeness", "provenance");
        return Kit.Pass("Screen ok — shortlist ready for gate #1.", new() { ["completeness"] = 1, ["provenance"] = 1 });
    }

    public Lesson? LessonFor(string trigger, AgentContext ctx) => trigger == "MISSING_CRITERIA"
        ? Kit.Lesson(Id, ctx, trigger, $"In {ctx.Features.Sector}, always echo criteriaUsed (the thresholds you enforced) so a reviewer can audit the rank.")
        : null;
}
