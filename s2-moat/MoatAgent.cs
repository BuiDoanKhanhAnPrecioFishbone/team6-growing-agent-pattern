using System.Text.Json.Nodes;
using AIAssistant.AgentHost;
using AIAssistant.Harness;

namespace AIAssistant.Agents;

/// <summary>S2 · Moat — cited business summary + draft moat. Learnable flaw: inventing a citation.</summary>
public sealed class MoatAgent : IAgent
{
    public string Id => "s2-moat";

    public Task<string> GenerateAsync(AgentContext ctx, IReadOnlyList<Lesson> lessons, string? critique, string? prior, int attempt, CancellationToken ct)
    {
        string Src(int i) => ctx.AllowedSources.Count > 0 ? ctx.AllowedSources[i % ctx.AllowedSources.Count] : "provided source";
        var third = Kit.Corrected(lessons, critique, "UNCITED_SOURCE") ? Src(2) : "Industry outlook 2027 (analyst estimate)";
        var block = new JsonObject
        {
            ["businessSummary"] = $"{ctx.Input["name"]?.GetValue<string>()} is a leading consumer-staples company in Vietnam with a market-leading brand and nationwide distribution, driving durable pricing power and repeat demand across a >40% share of its core segment.",
            ["moatType"] = "intangible_assets", ["moatStrength"] = "wide", ["moatTrend"] = "stable", ["circleOfCompetence"] = true,
            ["evidence"] = new JsonArray(
                new JsonObject { ["claim"] = "Market-leading brand with the largest domestic share, supporting pricing power.", ["source"] = Src(0) },
                new JsonObject { ["claim"] = "Nationwide distribution network gives a scale/cost advantage over rivals.", ["source"] = Src(1) },
                new JsonObject { ["claim"] = "Consistently high gross margins indicate a durable brand advantage.", ["source"] = third }),
            ["humanConfirmed"] = false,
        };
        return Task.FromResult(block.ToJsonString(Kit.J));
    }

    public Reward Evaluate(string draft, AgentContext ctx)
    {
        var b = Kit.Obj(draft);
        if (b["businessSummary"] is null || b["evidence"] is not JsonArray ev || ev.Count == 0)
            return Kit.Fail("SCHEMA", "GATE schema: moat needs businessSummary + ≥1 evidence.", "sourcing", "typeJustification");
        var uncited = ev.Select(e => e?["source"]?.GetValue<string>() ?? "")
                        .Where(s => !ctx.AllowedSources.Any(a => a.Contains(s) || s.Contains(a))).ToList();
        if (uncited.Count > 0)
            return Kit.Fail("UNCITED_SOURCE", $"GATE cite-or-drop: cite only provided sources — invented: {string.Join("; ", uncited)}.", "sourcing", "typeJustification");
        return Kit.Pass("Moat draft grounded — ready for gate #2.", new() { ["sourcing"] = 1, ["typeJustification"] = 1 });
    }

    public Lesson? LessonFor(string trigger, AgentContext ctx) => trigger == "UNCITED_SOURCE"
        ? Kit.Lesson(Id, ctx, trigger, $"In {ctx.Features.Sector}, cite ONLY the provided sources — never invent a page, URL or 'analyst estimate'.")
        : null;
}
