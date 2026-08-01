using System.Text.RegularExpressions;
using AIAssistant.Harness;

// ─────────────────────────────────────────────────────────────────────────────
// ablate — the ABLATION table. "Provably better" shouldn't be a claim; it should be
// a number per lever. Same cheap model, same task suite, levers switched on one at a
// time — so each row shows its OWN marginal contribution to quality and to cost.
//
//   bare            one completion, no harness            (baseline)
//   +revise loop    reward critique → fix, don't restart  (within-run)
//   +memory         lessons carry across the suite        (run-to-run)
//   +best-of-N      draw N drafts, keep the best          (inference-time compute)
//   +self-verify    an LLM critic objects to soft errors  (extra revise signal)
//   +escalation     hard residual → a stronger model      (only when set)
//
// The task is a CONSTRAINED note (5 gradable rules via Checks) — so the reward gives
// an actionable critique the loop can act on, and a lesson learned on one ticker
// (e.g. "always add a risk disclaimer") transfers to the next. Live-only.
// ─────────────────────────────────────────────────────────────────────────────

if (!ToolLoop.Enabled)
{
    Console.WriteLine("""
        ablate needs a live model. Set your Foundry deployment, then re-run:

          $env:AGENT_LLM_BASE_URL = "https://<resource>.openai.azure.com/openai/v1"
          $env:AGENT_LLM_API_KEY  = "<key>"
          $env:AGENT_LLM_MODEL    = "gpt-4.1-mini"
          # optional, enables the +escalation row:
          $env:AGENT_LLM_MODEL_STRONG = "gpt-5.1"
          dotnet run --project ablate
        """);
    return;
}

var tickers = new[] { "VNM", "FPT", "HPG", "MWG" };
var strong = Environment.GetEnvironmentVariable("AGENT_LLM_MODEL_STRONG");

AgentContext Ctx(string ticker) => new()
{
    Ticker = ticker,
    Features = new AgentFeatures("advisory", Array.Empty<string>(), $"one-line investment note for {ticker}"),
    Input = new System.Text.Json.Nodes.JsonObject { ["ticker"] = ticker },
    AllowedSources = Array.Empty<string>(),
};

// ── one ablation config = a set of toggles; run the whole suite under it and average ──
async Task<(double score, int calls, long tokens)> Run(string label, bool harness, int iters, int samples, bool memory, bool critic, bool escalate)
{
    var storePath = Path.Combine(Path.GetTempPath(), $"ablate-{Guid.NewGuid():N}.json");
    var store = memory ? (ILessonStore)new SemanticLessonStore(storePath) : new JsonLessonStore(storePath);
    ICritic? crit = critic ? new LlmCritic() : null;
    EscalateDraft? esc = (escalate && !string.IsNullOrWhiteSpace(strong))
        ? async (ag, ctx, lessons, critique, ct) =>
            { var (s, u) = NoteAgent.Prompt(ctx, lessons, critique, null); return await ToolLoop.CompleteAsync(s, u, 0.2, ct, strong); }
        : null;

    var h = new AgentHarness(store, critic: crit, escalate: esc);
    var opt = new HarnessOptions(MaxIters: iters, Threshold: 1.0, RetrieveTopK: 3, Samples: samples);

    double total = 0; int calls = 0;
    CostLedger.Reset();
    foreach (var t in tickers)
    {
        if (!harness)
        {
            var (sys, user) = NoteAgent.Prompt(Ctx(t), Array.Empty<Lesson>(), null, null);
            var draft = await ToolLoop.CompleteAsync(sys, user, 0.2);
            total += NoteAgent.Grade(draft, t).Score; calls += 1;
        }
        else
        {
            var o = await h.RunAsync(new NoteAgent(), Ctx(t), opt, default);
            total += o.Best.Score; calls += o.Generations;
        }
    }
    var usage = CostLedger.Snapshot();
    return (total / tickers.Length, calls, usage.Prompt + usage.Completion);
}

Console.WriteLine($"ablate · model={Environment.GetEnvironmentVariable("AGENT_LLM_MODEL")} · suite={tickers.Length} tickers · strong={(string.IsNullOrWhiteSpace(strong) ? "(unset)" : strong)}\n");

var configs = new List<(string label, Func<Task<(double, int, long)>> run)>
{
    ("bare (no harness)",  () => Run("bare",   harness:false, iters:1, samples:1, memory:false, critic:false, escalate:false)),
    ("+revise loop",       () => Run("revise", harness:true,  iters:3, samples:1, memory:false, critic:false, escalate:false)),
    ("+memory",            () => Run("memory", harness:true,  iters:3, samples:1, memory:true,  critic:false, escalate:false)),
    ("+best-of-N (N=3)",   () => Run("bestof", harness:true,  iters:3, samples:3, memory:true,  critic:false, escalate:false)),
    ("+self-verify",       () => Run("verify", harness:true,  iters:3, samples:3, memory:true,  critic:true,  escalate:false)),
};
if (!string.IsNullOrWhiteSpace(strong))
    configs.Add(("+escalation", () => Run("escalate", harness:true, iters:3, samples:3, memory:true, critic:true, escalate:true)));

Console.WriteLine($"{"lever (cumulative)",-22} {"quality",-9} {"Δ",-7} {"calls",-7} {"tokens",-8}");
Console.WriteLine(new string('─', 56));
double? prev = null;
foreach (var (label, run) in configs)
{
    var (score, calls, tokens) = await run();
    var delta = prev is null ? "  —" : (score - prev.Value >= 0 ? "+" : "") + ((score - prev.Value) * 100).ToString("0.0") + "pp";
    Console.WriteLine($"{label,-22} {score * 100,6:0.0}%   {delta,-7} {calls,-7} {tokens,-8}");
    prev = score;
}
Console.WriteLine(new string('─', 56));
Console.WriteLine("quality = mean reward across the suite (5 constraints, partial credit) · calls = model generations · Δ = marginal lift over the row above.");

// ─────────────────────────────────────────────────────────────────────────────
// The agent under test: write a one-line note that satisfies 5 checkable constraints.
// Its reward is deterministic (Checks), so the ablation is reproducible; its lessons
// are generic ("add a risk disclaimer"), so they transfer across tickers — memory
// earns its row honestly, not by memorizing a test answer.
// ─────────────────────────────────────────────────────────────────────────────
sealed class NoteAgent : IAgent
{
    public string Id => "note-advisor";

    public static (string sys, string user) Prompt(AgentContext ctx, IReadOnlyList<Lesson> lessons, string? critique, string? prior)
    {
        var sys = "You are an equity analyst. Write ONE sentence recommending an action on the given ticker.";
        if (lessons.Count > 0)
            sys += "\n\nRules you've learned — follow every one:" + string.Concat(lessons.Select(l => "\n• " + l.Warning));
        var user = $"Ticker: {ctx.Ticker}. Write the note.";
        if (!string.IsNullOrWhiteSpace(prior))
            user += $"\n\nYour previous attempt:\n{prior}\n\nProblems to fix:\n{critique}\n\nRewrite it, fixing every problem.";
        return (sys, user);
    }

    public async Task<string> GenerateAsync(AgentContext ctx, IReadOnlyList<Lesson> lessons, string? critique, string? priorDraft, int attempt, CancellationToken ct)
    {
        var (sys, user) = Prompt(ctx, lessons, critique, priorDraft);
        return await ToolLoop.CompleteAsync(sys, user, 0.2 + 0.1 * (attempt % 3), ct); // vary temp across samples
    }

    public Reward Evaluate(string draft, AgentContext ctx) => Grade(draft, ctx.Ticker);

    // the 5 constraints — deterministic, partial-credit; the failed labels become the learning triggers.
    public static Reward Grade(string note, string ticker)
    {
        var t = (note ?? "").ToLowerInvariant();
        var words = Regex.Matches(t, "[a-z0-9']+").Count;
        return Checks.Of(
            ("names the ticker",        t.Contains(ticker.ToLowerInvariant())),
            ("states a buy/hold/sell",  new[] { "buy", "hold", "sell", "accumulate", "reduce", "overweight", "underweight" }.Any(t.Contains)),
            ("gives a reason",          new[] { "because", "due to", "driven by", "given", "on strong", "on weak", "thanks to" }.Any(t.Contains)),
            ("includes a risk note",    t.Contains("risk")),
            ("is concise (≤30 words)",  words > 0 && words <= 30));
    }

    public Lesson? LessonFor(string trigger, AgentContext ctx)
    {
        var warning = trigger switch
        {
            "includes a risk note"    => "Always include a brief risk caveat (mention risk).",
            "is concise (≤30 words)"  => "Keep the note to one sentence, under 30 words.",
            "gives a reason"          => "Justify the call with 'because' or 'due to'.",
            "states a buy/hold/sell"  => "State an explicit buy/hold/sell action.",
            "names the ticker"        => "Name the ticker explicitly in the note.",
            _ => null,
        };
        if (warning is null) return null;
        return new Lesson
        {
            Id = $"{Id}|advisory|{trigger}", Agent = Id, Sector = "advisory", Trigger = trigger,
            Condition = "writing a one-line investment note", Warning = warning, Type = LessonType.Strategy,
        };
    }
}
