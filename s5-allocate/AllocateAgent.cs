using System.Text.Json.Nodes;
using AIAssistant.AgentHost;
using AIAssistant.Harness;

namespace AIAssistant.Agents;

/// <summary>S5 · Allocate — decision + position size. Learnable flaw: dropping the disclaimer.</summary>
public sealed class AllocateAgent : IAgent
{
    public string Id => "s5-allocate";

    public Task<string> GenerateAsync(AgentContext ctx, IReadOnlyList<Lesson> lessons, string? critique, string? prior, int attempt, CancellationToken ct)
    {
        var val = ctx.Input["valuation"] as JsonObject;
        var mos = val?["margin_of_safety"]?["vs_mid"]?.GetValue<double>() ?? 0;
        var lo = val?["intrinsic_value_range"]?["low"]?.GetValue<double>() ?? 0;
        var hi = val?["intrinsic_value_range"]?["high"]?.GetValue<double>() ?? 0;
        var mid = (lo + hi) / 2;
        var buy = mos >= 0.20;
        var block = new JsonObject
        {
            ["decision"] = buy ? "buy" : "hold",
            ["positionSizePct"] = buy ? 0.03 : 0.0,
            ["entryTarget"] = buy ? Math.Round(mid * 0.80) : null,
            ["thesis"] = $"Wide, stable moat and grade-A financials with ~{mos:P0} margin of safety versus intrinsic value support a starter position.",
            ["sizingBreakdown"] = new JsonObject
            {
                ["conviction"] = "medium", ["convictionTarget"] = 0.05,
                ["clamps"] = new JsonArray(new JsonObject { ["rule"] = "sector_cap", ["detail"] = "consumer_staples headroom", ["cappedTo"] = 0.03 }),
                ["final"] = buy ? 0.03 : 0.0,
            },
            ["humanConfirmed"] = false,
        };
        if (Kit.Corrected(lessons, critique, "MISSING_DISCLAIMER"))
            block["disclaimer"] = "Educational/illustrative; not investment advice.";
        return Task.FromResult(block.ToJsonString(Kit.J));
    }

    public Reward Evaluate(string draft, AgentContext ctx)
    {
        var b = Kit.Obj(draft);
        if (b["decision"]?.GetValue<string>() is not { } decision || b["positionSizePct"] is null)
            return Kit.Fail("SCHEMA", "GATE schema: allocation needs decision + positionSizePct.", "grounded", "sizing");
        if (decision == "buy" && string.IsNullOrEmpty(b["disclaimer"]?.GetValue<string>()))
            return Kit.Fail("MISSING_DISCLAIMER", "GATE compliance: a buy recommendation must carry the not-advice disclaimer.", "grounded", "sizing");
        return Kit.Pass($"Allocation: {decision} — ready for gate #4.", new() { ["grounded"] = 1, ["sizing"] = 1 });
    }

    public Lesson? LessonFor(string trigger, AgentContext ctx) => trigger == "MISSING_DISCLAIMER"
        ? Kit.Lesson(Id, ctx, trigger, $"For {ctx.Features.Sector}, every buy recommendation must include the educational/not-advice disclaimer.")
        : null;
}
