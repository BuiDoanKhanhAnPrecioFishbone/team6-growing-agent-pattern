namespace AIAssistant.Harness;

// ─────────────────────────────────────────────────────────────────────────────
// DataValue — put a number on the free training data. Every run mines two assets a
// team would normally PAY for: verified lessons (curated rules) and labeled training
// examples (the flywheel's SFT / preference / RL lines — each a graded example a human
// would otherwise have to write and label). This estimates the labeled-example
// equivalent and, at a configurable per-example rate, the labeling spend avoided.
//
// Honest by construction: the count is exact; the dollar figure is an ESTIMATE at
// AGENT_DATA_PRICE (typical human-labeling cost per example) — presented as "≈".
// ─────────────────────────────────────────────────────────────────────────────
public static class DataValue
{
    public sealed record Report(
        int Lessons, int VerifiedLessons,
        int SftExamples, int PreferencePairs, int RlSamples,
        int LabeledExamples, double PricePerExample, double DollarsAvoided);

    /// <summary>Estimate the value mined. <paramref name="pricePerExample"/> overrides AGENT_DATA_PRICE (default $0.20).</summary>
    public static Report Estimate(IReadOnlyList<Lesson> lessons, int sft, int preference, int rl, double? pricePerExample = null)
    {
        var price = pricePerExample ?? EnvD("AGENT_DATA_PRICE", 0.20);
        var verified = lessons.Count(l => l.Trust == Trust.Verified && string.IsNullOrEmpty(l.ValidTo));
        var labeled = sft + preference + rl;               // every exported line is a labeled training example
        return new Report(lessons.Count, verified, sft, preference, rl, labeled, price, Math.Round(labeled * price, 2));
    }

    /// <summary>A one-line readout for a console or UI.</summary>
    public static string Line(Report r) =>
        $"{r.VerifiedLessons} verified lessons · {r.LabeledExamples} labeled examples mined ≈ ${r.DollarsAvoided:0.00} of labeling avoided (at ${r.PricePerExample:0.00}/ex)";

    private static double EnvD(string k, double d) => double.TryParse(Environment.GetEnvironmentVariable(k), out var v) ? v : d;
}
