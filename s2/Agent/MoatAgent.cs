using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIAssistant.Analysis;
using AIAssistant.Domain;
using AIAssistant.Harness;

namespace AIAssistant.Agent;

/// <summary>
/// S2 · Moat as an <see cref="IAgent"/> in the harness. The reward is <see cref="MoatScorer"/>; the
/// environment is the allowed-source set it must cite. When no live model is configured it runs the
/// deterministic <see cref="MockMoatModel"/> so the loop — and its run-to-run learning — demos offline.
/// </summary>
public sealed class MoatAgent : IAgent
{
    private readonly ChatClient _chat;
    public MoatAgent(ChatClient chat) => _chat = chat;

    public string Id => "s2-moat";

    public async Task<string> GenerateAsync(
        AgentContext ctx, IReadOnlyList<Lesson> lessons,
        string? critique, string? priorDraft, int attempt, CancellationToken ct)
    {
        if (!_chat.Options.Enabled)
            return MockMoatModel.Generate(ctx, lessons, critique, attempt);

        var messages = new List<ChatMessage>
        {
            new("system", Prompts.System),
            new("user", Prompts.User(ctx, lessons)),
        };
        if (priorDraft is not null && critique is not null)
        {
            messages.Add(new("assistant", priorDraft));
            messages.Add(new("user", $"Your draft failed the reward. Fix ALL of the following and return the FULL corrected JSON:\n{critique}"));
        }
        return await _chat.CompleteAsync(messages, ct);
    }

    public Reward Evaluate(string draft, AgentContext ctx) => MoatScorer.Score(draft, ctx);

    public Lesson? LessonFor(string trigger, AgentContext ctx)
    {
        var sector = ctx.Features.Sector;
        string? warning = trigger switch
        {
            "UNCITED_SOURCE" => $"In {sector}, cite ONLY from the provided sources — prefer Annual Report > prospectus > official filings. Never invent a page, URL or 'analyst estimate'; if a claim has no provided source, drop it.",
            "TYPE_UNSUPPORTED" => $"When classifying a {sector} moat, include at least one cited bullet that names the mechanism (brand / switching cost / network / cost / scale) and matches the moatType.",
            "GENERIC_SUMMARY" => $"Ground every summary sentence in the {sector} sources — revenue mix, named markets/products, shares or growth rates. Drop generic description.",
            "THIN_SUMMARY" => $"If you mark a {sector} name in-circle, explain the model fully from cited sources (≥2 evidence bullets and a substantive summary).",
            "NOT_DRAFT" => "moatStrength/moatTrend are DRAFT proposals for the human gate — keep humanConfirmed=false, never present strength as fetched data.",
            _ => null, // SCHEMA and unknowns don't make useful guidance
        };
        if (warning is null) return null;

        return new Lesson
        {
            Id = $"{Id}|{sector}|{trigger}",
            Agent = Id,
            Sector = sector,
            Trigger = trigger,
            Warning = warning,
            LearnedFrom = ctx.Ticker,
        };
    }
}

/// <summary>The prompts, kept in one place so a future ART trainer reproduces them verbatim.</summary>
internal static class Prompts
{
    public const string System =
        """
        You are an equity analyst drafting the MOAT section (step S2) of a value-investing dossier.
        Output ONLY a JSON object with exactly these keys:
          businessSummary (string), moatType (one of: intangible_assets, switching_costs, network_effect,
          cost_advantage, efficient_scale, none), moatStrength (none|narrow|wide),
          moatTrend (widening|stable|narrowing), circleOfCompetence (boolean),
          evidence (array of {claim, source}, ≥1), humanConfirmed (boolean).

        Hard rules (violating any voids the draft):
        - CITE OR DROP: every evidence source MUST be one of the PROVIDED SOURCES. Never invent a citation.
        - Every sentence of businessSummary must trace to the cited evidence.
        - moatStrength and moatTrend are DRAFT PROPOSALS for a human gate — set humanConfirmed=false.
        - At least one evidence bullet must name the mechanism behind the chosen moatType.
        - Be specific: name markets, products, revenue mix, shares or growth rates from the sources.
        """;

    public static string User(AgentContext ctx, IReadOnlyList<Lesson> lessons)
    {
        var sb = new StringBuilder();
        sb.AppendLine("COMPANY:");
        sb.AppendLine(ctx.Input.ToJsonString(S2Json.Pretty));
        sb.AppendLine();
        sb.AppendLine("PROVIDED SOURCES (cite only these):");
        foreach (var s in ctx.AllowedSources) sb.AppendLine($"  - {s}");
        if (lessons.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("LESSONS FROM PAST RUNS (apply them — they were learned from earlier mistakes):");
            foreach (var l in lessons) sb.AppendLine($"  • {l.Warning}");
        }
        return sb.ToString();
    }
}

/// <summary>
/// Deterministic stand-in for a live model — makes the harness demoable offline (charter risk R1) and
/// makes the learning VISIBLE: on a fresh sector it invents one citation (tripping the cite-or-drop
/// gate); once the loop has fixed that and written a lesson, a later same-sector run has the lesson
/// injected up front and gets it right on the first attempt. Same behavior a trained policy converges to.
/// </summary>
internal static class MockMoatModel
{
    public static string Generate(AgentContext ctx, IReadOnlyList<Lesson> lessons, string? critique, int attempt)
    {
        var name = ctx.Input["name"]?.GetValue<string>() ?? ctx.Ticker;
        var (type, mechanism, sectorLabel) = Profile(ctx.Features.Sector);

        string Src(int i) => ctx.AllowedSources.Count > 0 ? ctx.AllowedSources[i % ctx.AllowedSources.Count] : "provided source";

        // The one learnable behavior: cite correctly. Corrected once a lesson is injected OR on revision.
        var knowsToCite = critique is not null || lessons.Any(l => l.Trigger == "UNCITED_SOURCE");
        var thirdSource = (!knowsToCite && attempt == 0)
            ? "Industry outlook 2027 (analyst estimate)"   // NOT in the provided set — trips UNCITED_SOURCE
            : Src(2);

        var draft = new MoatDraft
        {
            BusinessSummary =
                $"{name} is a leading {sectorLabel} company in Vietnam, generating revenue from " +
                $"{mechanism}-driven demand across a nationwide footprint. Its position rests on a durable " +
                $"{mechanism} advantage that supports pricing power and repeat demand, with more than 40% share of its core segment.",
            MoatType = type,
            MoatStrength = MoatStrength.Wide,
            MoatTrend = MoatTrend.Stable,
            CircleOfCompetence = true,
            HumanConfirmed = false,
            Evidence = new List<Evidence>
            {
                new() { Claim = $"{name} holds a market-leading {mechanism} position with the largest domestic share, supporting pricing power.", Source = Src(0) },
                new() { Claim = $"A nationwide distribution network gives {name} a {mechanism} and scale advantage versus smaller rivals.", Source = Src(1) },
                new() { Claim = $"Consistently high margins across the five-year window indicate a durable {mechanism} advantage.", Source = thirdSource },
            },
        };

        return JsonSerializer.Serialize(draft, S2Json.Options);
    }

    private static (MoatType Type, string Mechanism, string SectorLabel) Profile(string sector) =>
        sector.ToLowerInvariant() switch
        {
            var s when s.Contains("staple") || s.Contains("consumer") => (MoatType.IntangibleAssets, "brand", "consumer-staples"),
            var s when s.Contains("tech") || s.Contains("it") || s.Contains("software") => (MoatType.CostAdvantage, "low-cost scale", "technology"),
            var s when s.Contains("bank") || s.Contains("financ") => (MoatType.CostAdvantage, "cost", "financial-services"),
            var s when s.Contains("util") || s.Contains("energy") || s.Contains("gas") => (MoatType.EfficientScale, "efficient scale", "utilities"),
            _ => (MoatType.IntangibleAssets, "brand", "general"),
        };
}
