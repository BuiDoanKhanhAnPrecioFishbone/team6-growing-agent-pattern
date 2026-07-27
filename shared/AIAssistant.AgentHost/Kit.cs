using System.Text.Json;
using System.Text.Json.Nodes;
using AIAssistant.Harness;

namespace AIAssistant.AgentHost;

/// <summary>Authoring helpers shared by every agent — JSON, reward builders, lesson minting.</summary>
public static class Kit
{
    public static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    /// <summary>The mock model corrects its one learnable flaw once a lesson is injected or on a revision.</summary>
    public static bool Corrected(IReadOnlyList<Lesson> lessons, string? critique, string trigger) =>
        critique is not null || lessons.Any(l => l.Trigger == trigger);

    public static Reward Fail(string trigger, string critique, params string[] components) =>
        new(false, 0.0, components.ToDictionary(c => c, _ => 0.0), new HashSet<string> { trigger }, critique);

    public static Reward Pass(string critique, Dictionary<string, double> breakdown) =>
        new(true, Math.Round(breakdown.Values.Average(), 4), breakdown, new HashSet<string>(), critique);

    public static Lesson Lesson(string agent, AgentContext ctx, string trigger, string warning) => new()
    {
        Id = $"{agent}|{ctx.Features.Sector}|{trigger}",
        Agent = agent, Sector = ctx.Features.Sector, Trigger = trigger,
        Warning = warning, LearnedFrom = ctx.Ticker,
    };

    public static JsonObject Obj(string draft) => (JsonNode.Parse(draft) as JsonObject)!;
}
