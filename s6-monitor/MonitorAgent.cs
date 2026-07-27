using System.Text.Json.Nodes;
using AIAssistant.AgentHost;
using AIAssistant.Harness;

namespace AIAssistant.Agents;

/// <summary>S6 · Monitor — thesis tracking. Learnable flaw: an unsourced alert.</summary>
public sealed class MonitorAgent : IAgent
{
    public string Id => "s6-monitor";

    public Task<string> GenerateAsync(AgentContext ctx, IReadOnlyList<Lesson> lessons, string? critique, string? prior, int attempt, CancellationToken ct)
    {
        var sourced = Kit.Corrected(lessons, critique, "UNSOURCED_ALERT");
        var block = new JsonObject
        {
            ["thesisStatus"] = "intact",
            ["lastChecked"] = ctx.Input["asOf"]?.GetValue<string>() ?? "2026-07-07",
            ["alerts"] = new JsonArray(new JsonObject
            {
                ["type"] = "risk",
                ["detail"] = "Asset-turnover decline persists — watch revenue growth vs. the asset base.",
                ["source"] = sourced ? "BS2026Q1" : "",
            }),
            ["recommendReValue"] = false,
        };
        return Model.Generate(block.ToJsonString(Kit.J), ctx, lessons, critique, Id, ct);
    }

    public Reward Evaluate(string draft, AgentContext ctx)
    {
        var b = Kit.Obj(draft);
        if (b["thesisStatus"] is null || b["alerts"] is not JsonArray alerts)
            return Kit.Fail("SCHEMA", "GATE schema: monitoring needs thesisStatus + alerts.", "sourced", "consistency");
        if (alerts.Any(a => string.IsNullOrEmpty(a?["source"]?.GetValue<string>())))
            return Kit.Fail("UNSOURCED_ALERT", "GATE grounding: every alert must cite the statement/source it came from.", "sourced", "consistency");
        return Kit.Pass($"Monitoring: thesis {b["thesisStatus"]!.GetValue<string>()}.", new() { ["sourced"] = 1, ["consistency"] = 1 });
    }

    public Lesson? LessonFor(string trigger, AgentContext ctx) => trigger == "UNSOURCED_ALERT"
        ? Kit.Lesson(Id, ctx, trigger, $"For {ctx.Features.Sector}, every monitoring alert must cite its source (e.g. a statement period) — never emit an unsourced alert.")
        : null;
}
