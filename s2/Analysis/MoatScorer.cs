using System.Text.Json;
using System.Text.RegularExpressions;
using AIAssistant.Domain;
using AIAssistant.Harness;

namespace AIAssistant.Analysis;

/// <summary>
/// Grounding lint for the moat draft — the qualitative analog of S3's MemoLint. Where MemoLint rejects
/// numbers absent from the computed set, MoatLint rejects claims whose <c>source</c> is not among the
/// allowed sources (an invented citation) or is not locatable. "Cite or drop."
/// </summary>
public static class MoatLint
{
    // A source is "locatable" if it carries a page / section / URL / dataset / year — not a bare name.
    private static readonly Regex Locator = new(@"(p\.?\s*\d+|§|\bhttps?://|:\S|\b(19|20)\d{2}\b|\bpp\.?\s*\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Returns the offending sources: cited but not in the allowed set (when one is provided).</summary>
    public static List<string> Uncited(MoatDraft draft, IReadOnlyList<string> allowed)
    {
        var offenders = new List<string>();
        foreach (var e in draft.Evidence)
        {
            var src = e.Source?.Trim();
            if (string.IsNullOrEmpty(src)) { offenders.Add("(missing source)"); continue; }
            if (allowed.Count > 0 && !IsAllowed(src, allowed)) offenders.Add(src);
        }
        return offenders;
    }

    public static bool IsAllowed(string source, IReadOnlyList<string> allowed) =>
        allowed.Any(a => Contains(a, source) || Contains(source, a));

    public static bool IsLocatable(string? source) => !string.IsNullOrWhiteSpace(source) && Locator.IsMatch(source);

    private static bool Contains(string haystack, string needle)
    {
        var h = Norm(haystack); var n = Norm(needle);
        return n.Length >= 6 && h.Contains(n);
    }

    private static string Norm(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();
}

/// <summary>
/// The S2 reward. Hard gates zero out a malformed / mis-classed / ungrounded draft; graded components
/// reward sourcing quality, moat-type justification, summary grounding, specificity and circle-of-competence.
/// Deterministic ⇒ unhackable and reproducible ⇒ safe as both the loop gate and (later) the RL reward.
/// </summary>
public static class MoatScorer
{
    private static readonly (string Key, double Weight)[] Components =
    [
        ("sourcing", 0.30),
        ("moatTypeJustification", 0.25),
        ("summaryGrounding", 0.20),
        ("specificity", 0.15),
        ("circleOfCompetence", 0.10),
    ];

    private static readonly Dictionary<MoatType, string[]> TypeKeywords = new()
    {
        [MoatType.IntangibleAssets] = ["brand", "patent", "license", "trademark", "reputation", "regulat", "proprietary", "premium"],
        [MoatType.SwitchingCosts]   = ["switch", "lock-in", "lock in", "integrat", "migrat", "contract", "embedded", "sticky", "retention"],
        [MoatType.NetworkEffect]    = ["network", "platform", "marketplace", "users", "two-sided", "ecosystem", "liquidity"],
        [MoatType.CostAdvantage]    = ["cost advantage", "low-cost", "low cost", "scale", "arbitrage", "cheaper", "efficien", "margin"],
        [MoatType.EfficientScale]   = ["efficient scale", "niche", "natural monopoly", "regulated", "limited market", "single provider", "utility"],
        [MoatType.None]             = [],
    };

    private static readonly Regex NumericToken = new(@"\d[\d,]*(?:\.\d+)?%?", RegexOptions.Compiled);
    private static readonly Regex ProperNoun = new(@"\b[A-Z][a-zA-Z]{2,}\b", RegexOptions.Compiled);

    public static Reward Score(string? draftJson, AgentContext ctx)
    {
        var critique = new List<string>();
        var fails = new HashSet<string>();

        MoatDraft? draft = null;
        try { draft = JsonSerializer.Deserialize<MoatDraft>(draftJson ?? "", S2Json.Options); }
        catch { /* handled below */ }

        // ── GATE: schema ──
        if (draft is null || string.IsNullOrWhiteSpace(draft.BusinessSummary) ||
            draft.MoatType is null || draft.MoatStrength is null || draft.MoatTrend is null ||
            draft.CircleOfCompetence is null || draft.Evidence.Count == 0 ||
            draft.Evidence.Any(e => string.IsNullOrWhiteSpace(e.Claim) || string.IsNullOrWhiteSpace(e.Source)))
        {
            fails.Add("SCHEMA");
            return Fail(fails, "GATE schema: emit valid JSON with businessSummary, moatType, moatStrength, " +
                              "moatTrend, circleOfCompetence, humanConfirmed and ≥1 evidence bullet {claim, source}.");
        }

        // ── GATE: draft discipline — strength/trend are proposals, never fetched data ──
        if (draft.HumanConfirmed == true)
        {
            fails.Add("NOT_DRAFT");
            return Fail(fails, "GATE draft: moatStrength/moatTrend are DRAFT proposals for the human gate — " +
                              "set humanConfirmed=false; never present strength as confirmed data.");
        }

        // ── GATE: citations grounded (MoatLint) ──
        var uncited = MoatLint.Uncited(draft, ctx.AllowedSources);
        if (uncited.Count > 0)
        {
            fails.Add("UNCITED_SOURCE");
            return Fail(fails, $"GATE cite-or-drop: these sources are not among the provided sources — " +
                              $"cite only what was given (prefer Annual Report > prospectus > filings): {string.Join("; ", uncited.Distinct())}.");
        }

        // ── GRADED components ──
        var evidenceText = string.Join(" \n", draft.Evidence.Select(e => $"{e.Claim} {e.Source}"));
        var summary = draft.BusinessSummary!;

        var sourcing = Sourcing(draft, critique);
        var typeJust = MoatTypeJustification(draft, evidenceText, critique, fails);
        var grounding = SummaryGrounding(summary, evidenceText, ctx, critique, fails);
        var specificity = Specificity(summary, evidenceText, critique, fails);
        var coc = CircleOfCompetence(draft, summary, critique, fails);

        var breakdown = new Dictionary<string, double>
        {
            ["sourcing"] = sourcing,
            ["moatTypeJustification"] = typeJust,
            ["summaryGrounding"] = grounding,
            ["specificity"] = specificity,
            ["circleOfCompetence"] = coc,
        };
        var score = Math.Round(Components.Sum(c => c.Weight * breakdown[c.Key]), 4);

        if (critique.Count == 0)
            critique.Add("Draft is grounded and internally consistent — ready for Gate #2 (human confirms strength).");

        return new Reward(true, score, breakdown, fails, string.Join("\n", critique));
    }

    private static double Sourcing(MoatDraft draft, List<string> critique)
    {
        var strong = draft.Evidence.Count(e => MoatLint.IsLocatable(e.Source));
        var frac = (double)strong / draft.Evidence.Count;
        if (frac < 1.0)
            critique.Add("Sourcing: give every evidence bullet a locatable citation (page, section, URL or year), " +
                         "not just a document name.");
        return frac;
    }

    private static double MoatTypeJustification(MoatDraft draft, string evidenceText, List<string> critique, HashSet<string> fails)
    {
        var kws = TypeKeywords[draft.MoatType!.Value];
        if (kws.Length == 0) return 1.0; // "none" needs no corroboration
        var lower = evidenceText.ToLowerInvariant();
        var hits = draft.Evidence.Count(e => kws.Any((e.Claim ?? "").ToLowerInvariant().Contains));
        if (hits == 0)
        {
            fails.Add("TYPE_UNSUPPORTED");
            critique.Add($"Moat-type justification: no evidence bullet names the {Snake(draft.MoatType.Value)} mechanism. " +
                         "Add a cited bullet that shows WHY this is the moat, or change the type.");
            return 0.0;
        }
        return Math.Min(1.0, 0.5 + 0.5 * hits / draft.Evidence.Count);
    }

    private static double SummaryGrounding(string summary, string evidenceText, AgentContext ctx, List<string> critique, HashSet<string> fails)
    {
        var haystack = (evidenceText + " " + string.Join(" ", ctx.AllowedSources)).ToLowerInvariant();
        var sentences = Regex.Split(summary, @"(?<=[.!?])\s+").Where(s => s.Trim().Length > 0).ToList();
        if (sentences.Count == 0) return 0.0;

        var grounded = 0;
        foreach (var s in sentences)
        {
            var salient = Regex.Matches(s, @"\b[A-Za-z][A-Za-z0-9\-]{4,}\b")
                .Select(m => m.Value.ToLowerInvariant())
                .Where(t => !StopWords.Contains(t))
                .ToList();
            if (salient.Count == 0 || salient.Any(haystack.Contains)) grounded++;
        }
        var frac = (double)grounded / sentences.Count;
        if (frac < 0.6)
        {
            fails.Add("GENERIC_SUMMARY");
            critique.Add("Summary grounding: tie each sentence of the business summary to the cited evidence — " +
                         "drop or source any generic sentence.");
        }
        return frac;
    }

    private static double Specificity(string summary, string evidenceText, List<string> critique, HashSet<string> fails)
    {
        var text = summary + " " + evidenceText;
        var numbers = NumericToken.Matches(text).Count;
        var nouns = ProperNoun.Matches(text).Count;
        var score = Math.Min(1.0, (numbers >= 1 ? 0.5 : 0.0) + (nouns >= 3 ? 0.5 : Math.Min(0.5, nouns * 0.15)));
        if (score < 0.6)
        {
            fails.Add("GENERIC_SUMMARY");
            critique.Add("Specificity: ground the draft in concrete particulars — revenue mix, named markets/products, " +
                         "shares or growth rates from the sources.");
        }
        return Math.Round(score, 4);
    }

    private static double CircleOfCompetence(MoatDraft draft, string summary, List<string> critique, HashSet<string> fails)
    {
        if (draft.CircleOfCompetence != true) return 1.0; // "not in circle" defers cleanly to the human gate
        var explainable = summary.Length >= 120 && draft.Evidence.Count >= 2;
        if (!explainable)
        {
            fails.Add("THIN_SUMMARY");
            critique.Add("Circle-of-competence: you marked it in-circle, but the model isn't fully explained from sources — " +
                         "expand the summary or add cited evidence (else defer to the gate).");
            return 0.4;
        }
        return 1.0;
    }

    private static Reward Fail(HashSet<string> fails, string critique)
    {
        var zero = Components.ToDictionary(c => c.Key, _ => 0.0);
        return new Reward(false, 0.0, zero, fails, critique);
    }

    private static string Snake(MoatType t) => JsonNamingPolicy.SnakeCaseLower.ConvertName(t.ToString());

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "which", "their", "these", "those", "there", "where", "while", "about", "other", "under",
        "across", "through", "between", "company", "business", "products", "market", "revenue",
    };
}
