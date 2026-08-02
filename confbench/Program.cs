using AIAssistant.Harness;

// ─────────────────────────────────────────────────────────────────────────────
// confbench — confidence & abstention. In a huge domain the honest failure mode is
// bluffing on a case the agent has never seen. This proves the agent knows when it
// doesn't know: the SAME result signals map to Answer / Verify / Escalate / Abstain.
// Deterministic — outcomes are constructed to exercise each branch of the policy.
// ─────────────────────────────────────────────────────────────────────────────

int pass = 0, fail = 0;
void Check(string name, bool ok) { Console.WriteLine($"   [{(ok ? "PASS" : "FAIL")}] {name}"); if (ok) pass++; else fail++; }

HarnessOutcome Outcome(bool passed, double score, string[] injected, (double s, bool p)[] attempts, bool escalated = false) =>
    new(BestDraft: "…", Best: Reward.Scored(passed, score),
        Iterations: 1, FirstScore: score, InjectedLessons: injected, LearnedLessons: Array.Empty<string>(),
        Generations: attempts.Length, Escalated: escalated,
        Attempts: attempts.Select(a => new Attempt("…", a.s, a.p)).ToList());

Console.WriteLine("confbench — does the agent know when it doesn't know?\n");
Console.WriteLine($"{"scenario",-46}{"conf",-7}{"action",-10}\n" + new string('─', 63));

void Show(string name, Confidence c) => Console.WriteLine($"{name,-46}{c.Score * 100,4:0}%   {c.Action,-10}");

// 1. clean pass, had a relevant lesson, attempts agree → ANSWER (high confidence)
var c1 = ConfidencePolicy.Assess(Outcome(true, 1.0, new[] { "l1" }, new[] { (1.0, true), (1.0, true) }));
Show("charted: pass + recall + agreement", c1);
Check("→ Answer, high confidence", c1.Action == AgentAction.Answer && c1.Score >= 0.9);

// 2. passed, but a NOVEL case (no recall) scraped through on one lucky draft → VERIFY
var c2 = ConfidencePolicy.Assess(Outcome(true, 1.0, Array.Empty<string>(), new[] { (1.0, true), (0.4, false), (0.4, false) }));
Show("novel: pass, no recall, low agreement", c2);
Check("→ Verify (answer, but flag it)", c2.Action == AgentAction.Verify && c2.WillAnswer);

// 3. below the bar, a stronger model is available and we haven't escalated yet → ESCALATE
var c3 = ConfidencePolicy.Assess(Outcome(false, 0.5, Array.Empty<string>(), new[] { (0.5, false), (0.5, false) }), strongModelAvailable: true);
Show("hard: below bar, strong model available", c3);
Check("→ Escalate", c3.Action == AgentAction.Escalate);

// 4. still below the bar after escalation → ABSTAIN (ask for more; don't bluff)
var c4 = ConfidencePolicy.Assess(Outcome(false, 0.4, Array.Empty<string>(), new[] { (0.4, false), (0.4, false) }, escalated: true), strongModelAvailable: true);
Show("uncharted: below bar even after escalate", c4);
Check("→ Abstain (won't bluff)", c4.Action == AgentAction.Abstain && !c4.WillAnswer);

Console.WriteLine("\nwhat the user hears when it won't answer outright:");
Console.WriteLine($"   verify   → {ConfidencePolicy.Message(c2)}");
Console.WriteLine($"   escalate → {ConfidencePolicy.Message(c3)}");
Console.WriteLine($"   abstain  → {ConfidencePolicy.Message(c4)}");

Console.WriteLine($"\n{pass} passed, {fail} failed.");
Console.WriteLine(fail == 0
    ? "\nVerdict: on uncharted ground the agent abstains or escalates instead of bluffing — and the miss becomes the next lesson."
    : "\nVerdict: the confidence policy regressed — investigate.");
Environment.Exit(fail == 0 ? 0 : 1);
