using System.Text.Json.Nodes;
using AIAssistant.AgentHost;
using AIAssistant.Harness;

namespace AIAssistant.Agents;

/// <summary>S4 · Valuation — intrinsic value + margin of safety. Learnable flaw: an untyped assumption.</summary>
public sealed class ValuationAgent : IAgent
{
    public string Id => "s4-valuation";

    public Task<string> GenerateAsync(AgentContext ctx, IReadOnlyList<Lesson> lessons, string? critique, string? prior, int attempt, CancellationToken ct)
    {
        var typed = Kit.Corrected(lessons, critique, "UNTYPED_ASSUMPTION");
        var block = new JsonObject
        {
            ["assumptions"] = new JsonArray(
                new JsonObject { ["name"] = "fcf_growth", ["value"] = 0.03, ["basis"] = "computed" },
                new JsonObject { ["name"] = "discount_rate", ["value"] = 0.1143, ["basis"] = typed ? "computed" : "" }),
            ["discount_rate"] = 0.1143,
            ["methods"] = new JsonObject { ["s4-dcf"] = 24000, ["s4-rim"] = 22500, ["s4-multiples"] = 23000, ["s4-owner-earnings"] = 22800 },
            ["intrinsic_value_range"] = new JsonObject { ["low"] = 21000, ["high"] = 25000 },
            ["margin_of_safety"] = new JsonObject { ["vs_mid"] = 0.213 },
            ["price"] = new JsonObject { ["value"] = 18100, ["unit"] = "VND/share", ["as_of"] = "2026-07-01" },
            ["flags"] = new JsonArray(),
            ["gate3"] = new JsonObject { ["status"] = "gate3_pending", ["card"] = "Confirm growth + discount-rate assumptions." },
        };
        return Model.Generate(block.ToJsonString(Kit.J), ctx, lessons, critique, Id, ct);
    }

    public Reward Evaluate(string draft, AgentContext ctx)
    {
        var b = Kit.Obj(draft);
        if (b["intrinsic_value_range"] is null || b["margin_of_safety"] is null || b["assumptions"] is not JsonArray asm)
            return Kit.Fail("SCHEMA", "GATE schema: valuation needs assumptions, intrinsic_value_range and margin_of_safety.", "assumptions", "safety");
        if (ctx.Input["moat"]?["humanConfirmed"]?.GetValue<bool>() != true)
            return Kit.Fail("MOAT_UNCONFIRMED", "GATE ordering: valuation requires the confirmed moat (gate #2) — moat.humanConfirmed must be true.", "assumptions", "safety");
        if (asm.Any(a => string.IsNullOrEmpty(a?["basis"]?.GetValue<string>())))
            return Kit.Fail("UNTYPED_ASSUMPTION", "GATE provenance: every assumption needs a basis (computed | cited | human_override).", "assumptions", "safety");
        if (b["gate3"]?["status"]?.GetValue<string>() == "confirmed")
            return Kit.Fail("GATE3_SELFCONFIRM", "GATE draft: gate3 is confirmed by a human, not the agent — emit gate3_pending.", "assumptions", "safety");
        return Kit.Pass("Valuation drafted — assumptions typed, ready for gate #3.", new() { ["assumptions"] = 1, ["safety"] = 1 });
    }

    public Lesson? LessonFor(string trigger, AgentContext ctx) => trigger == "UNTYPED_ASSUMPTION"
        ? Kit.Lesson(Id, ctx, trigger, $"For {ctx.Features.Sector}, type EVERY valuation assumption by basis (computed | cited | human_override) — never leave basis empty.")
        : null;
}
