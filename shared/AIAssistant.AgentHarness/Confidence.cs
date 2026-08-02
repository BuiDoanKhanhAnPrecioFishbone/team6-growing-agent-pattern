namespace AIAssistant.Harness;

// ─────────────────────────────────────────────────────────────────────────────
// CONFIDENCE & ABSTENTION — the honest move in a LARGE domain. A cheap model on a huge
// surface will hit cases it has no experience with. Bluffing there is the real risk;
// the right behavior is to know when it doesn't know and ASK or ESCALATE instead — and
// the miss becomes the next lesson. This turns a run into a confidence score + a
// recommended action, from signals the harness already produces:
//   • the reward score + whether it cleared the bar,
//   • recall — did any lesson apply? (charted ground vs terra incognita),
//   • stability — did the attempts agree, or did one lucky draft scrape a pass?
// Post-hoc and non-invasive: the loop is unchanged; the caller decides what to do.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>What to do with a result given its confidence.</summary>
public enum AgentAction { Answer, Verify, Escalate, Abstain }

public sealed record Confidence(double Score, AgentAction Action, string Reason)
{
    /// <summary>True when the agent may surface an answer (outright or with a caveat).</summary>
    public bool WillAnswer => Action is AgentAction.Answer or AgentAction.Verify;
}

public static class ConfidencePolicy
{
    /// <summary>Assess a run. <paramref name="strongModelAvailable"/> lets a still-uncertain result recommend
    /// escalation before it falls all the way to abstaining.</summary>
    public static Confidence Assess(HarnessOutcome o, double threshold = 0.8, bool strongModelAvailable = false)
    {
        var passed = o.Best.Pass && o.Best.Score >= threshold;
        var recall = o.InjectedLessons.Count > 0;         // did the agent have relevant experience to lean on?
        var stability = Stability(o.Attempts);            // did its attempts agree (vs one lucky draft)?
        var conf = Math.Clamp(o.Best.Score * (0.85 + 0.15 * stability) * (recall ? 1.0 : 0.9), 0, 1);

        if (passed && conf >= 0.85)
            return new Confidence(conf, AgentAction.Answer, recall ? "cleared the bar on charted ground" : "cleared the bar");
        if (passed)
            return new Confidence(conf, AgentAction.Verify, "passed, but novel or unstable — verify the key facts before relying");
        if (!o.Escalated && strongModelAvailable)
            return new Confidence(conf, AgentAction.Escalate, "below the bar — route to a stronger model");
        return new Confidence(conf, AgentAction.Abstain, "below the bar even after escalation — abstain and ask for more");
    }

    /// <summary>A human-facing line for when the agent will not answer outright (empty for a confident Answer).</summary>
    public static string Message(Confidence c) => c.Action switch
    {
        AgentAction.Verify   => "I'm fairly confident, but this is unfamiliar — please double-check the key figures.",
        AgentAction.Escalate => "This one is hard — routing it to a stronger model.",
        AgentAction.Abstain  => "I'm not confident enough to answer this reliably yet. I can escalate, or you can give me more detail.",
        _                    => "",
    };

    // stability: the share of this run's attempts that passed. Two+ attempts agreeing ⇒ a stable result;
    // one pass among many fails ⇒ a lucky draft, lower confidence. Neutral when there's too little to judge.
    private static double Stability(IReadOnlyList<Attempt>? attempts)
    {
        if (attempts is null || attempts.Count < 2) return 0.75;
        return (double)attempts.Count(a => a.Pass) / attempts.Count;
    }
}
