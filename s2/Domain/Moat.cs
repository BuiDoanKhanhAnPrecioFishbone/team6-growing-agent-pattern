using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIAssistant.Domain;

// The S2 output vocabulary — mirrors skills/moat/schema.json. Enum members serialize snake_case
// (IntangibleAssets -> "intangible_assets") via the shared S2Json converter.
public enum MoatType { IntangibleAssets, SwitchingCosts, NetworkEffect, CostAdvantage, EfficientScale, None }
public enum MoatStrength { None, Narrow, Wide }
public enum MoatTrend { Widening, Stable, Narrowing }

/// <summary>One cited claim. The unit of grounding: a claim with no locatable source is dropped at the gate.</summary>
public sealed class Evidence
{
    public string? Claim { get; set; }
    public string? Source { get; set; }
}

/// <summary>
/// The moat draft — S2's product, written into the candidate file's <c>moat</c> key. It is always a
/// DRAFT (<see cref="HumanConfirmed"/> = false): strength/trend are proposals for Gate #2, never
/// presented as fetched data. Nullable members let the reward detect a malformed draft at the schema gate.
/// </summary>
public sealed class MoatDraft
{
    public string? BusinessSummary { get; set; }
    public MoatType? MoatType { get; set; }
    public MoatStrength? MoatStrength { get; set; }
    public MoatTrend? MoatTrend { get; set; }
    public bool? CircleOfCompetence { get; set; }
    public List<Evidence> Evidence { get; set; } = new();
    public bool? HumanConfirmed { get; set; }
}

/// <summary>Shared JSON options: web casing for fields, snake_case for enums, nulls omitted on write.</summary>
public static class S2Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static readonly JsonSerializerOptions Pretty = new(Options) { WriteIndented = true };
}
