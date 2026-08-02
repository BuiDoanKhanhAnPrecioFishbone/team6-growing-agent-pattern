using System.Text.Json;
using System.Text.Json.Nodes;
using AIAssistant.Harness;

// ─────────────────────────────────────────────────────────────────────────────
// slowloop — MOVE 1: close BOTH loops and show compounding. One deterministic run of
// the whole arc (offline, no model, no GPU), the way flywheel/memcon are:
//
//   Phase A · fast loop      run the suite over N sessions; the agent learns lessons
//                            in-context → quality holds while per-task COST falls.
//   Phase B · ReST-EM export filter the PASSING trajectories → a real chat SFT dataset
//                            (verified-gated) → the exact Foundry fine-tune command.
//   Phase C · bake + graduate on the baked model, re-test each lesson WITHOUT injecting
//                            it; the weights now know it → evict. Memory shrinks to zero,
//                            context cost → 0, quality HOLDS. Knowledge moved to weights.
//
// Emits compounding.json (a per-session series) for the curve artifact.
// ─────────────────────────────────────────────────────────────────────────────

const string Agent = "note-advisor", Sector = "advisory";
var tickers = new[] { "VNM", "FPT", "HPG" };
const int Sessions = 5;

var storePath = Path.Combine(Path.GetTempPath(), "slowloop-lessons.json"); File.Delete(storePath);
var store = new SemanticLessonStore(storePath);
var harness = new AgentHarness(store);
var opt = new HarnessOptions(MaxIters: 3, Threshold: 1.0, RetrieveTopK: 3, Samples: 1);

AgentContext Ctx(string ticker) => new()
{
    Ticker = ticker,
    Features = new AgentFeatures(Sector, Array.Empty<string>(), $"one-line investment note for {ticker}"),
    Input = new JsonObject { ["ticker"] = ticker },
    AllowedSources = Array.Empty<string>(),
};

int LessonTokens() =>
    store.AllAsync().Result.Where(l => l.Agent == Agent && l.Trust != Trust.Quarantined).Sum(l => Context.EstimateTokens(l.Warning));

var series = new JsonArray();
var runs = new List<(string system, string task, HarnessOutcome outcome)>();

Console.WriteLine("slowloop — closing both loops (offline, deterministic)\n");
Console.WriteLine("Phase A · fast loop: the agent learns in-context; quality holds, cost falls\n");
Console.WriteLine($"{"session",-9}{"quality",-9}{"calls/task",-12}{"lessons",-9}{"ctx tokens",-11}");
Console.WriteLine(new string('─', 50));

// ── baseline: the bare model, no harness, no lessons — where it starts ──
{
    double q = 0;
    foreach (var t in tickers) { var d = await new NoteAgent(baked: false).GenerateAsync(Ctx(t), Array.Empty<Lesson>(), null, null, 0, default); q += NoteAgent.Grade(d, t).Score; }
    var quality = q / tickers.Length;
    series.Add(new JsonObject { ["session"] = 0, ["phase"] = "baseline", ["quality"] = Math.Round(quality, 3), ["calls"] = 1.0, ["lessons"] = 0, ["ctxTokens"] = 0 });
    Console.WriteLine($"{"bare",-9}{quality * 100,6:0.0}%  {1.0,-12:0.00}{0,-9}{0,-11}");
}

// ── Phase A: fast loop over sessions ──
for (var s = 1; s <= Sessions; s++)
{
    double q = 0; int calls = 0;
    foreach (var t in tickers)
    {
        var o = await harness.RunAsync(new NoteAgent(baked: false), Ctx(t), opt, default);
        q += o.Best.Score; calls += o.Generations;
        runs.Add((NoteAgent.SystemPrompt, $"Ticker: {t}. Write the note.", o));
    }
    var quality = q / tickers.Length;
    var callsPer = (double)calls / tickers.Length;
    var lessons = (await store.AllAsync()).Count(l => l.Agent == Agent && l.Trust != Trust.Quarantined);
    var tok = LessonTokens();
    series.Add(new JsonObject { ["session"] = s, ["phase"] = "learning", ["quality"] = Math.Round(quality, 3), ["calls"] = Math.Round(callsPer, 2), ["lessons"] = lessons, ["ctxTokens"] = tok });
    Console.WriteLine($"{s,-9}{quality * 100,6:0.0}%  {callsPer,-12:0.00}{lessons,-9}{tok,-11}");
}

// ── Phase B: ReST-EM export — rejection-sample the passing trajectories into a chat SFT set ──
Console.WriteLine("\nPhase B · ReST-EM export (verified-gated: only passing trajectories)\n");
var sft = RestEm.Select(runs, threshold: 1.0);
var jsonl = RestEm.ToChatJsonl(sft);
var dir = Path.Combine(Path.GetTempPath(), "slowloop-dataset"); Directory.CreateDirectory(dir);
var sftPath = Path.Combine(dir, "sft.jsonl"); File.WriteAllText(sftPath, jsonl);
Console.WriteLine($"  {runs.Count} runs → {sft.Count} SFT samples (best passing completion per task)");
Console.WriteLine($"  written to {sftPath}");
Console.WriteLine($"  sample: {Clip(jsonl.Split('\n').FirstOrDefault())}");
Console.WriteLine("\n  to bake for real:");
Console.WriteLine("  " + RestEm.FoundrySubmitHint(sftPath, "gpt-4.1-mini").Replace("\n", "\n  "));

// ── Phase C: bake + graduate — on the baked model, prune every lesson the weights absorbed ──
Console.WriteLine("\nPhase C · bake + graduate: prune lessons the weights now know\n");
var baked = new NoteAgent(baked: true);
var grad = await Graduation.RunAsync(Agent, store,
    scoreOnBakedWithoutLesson: (lesson, ct) =>
    {
        // score the baked model on a representative task WITHOUT injecting any lesson
        var draft = baked.GenerateAsync(Ctx(tickers[0]), Array.Empty<Lesson>(), null, null, 0, ct).Result;
        return Task.FromResult(baked.Evaluate(draft, Ctx(tickers[0])).Score);
    },
    passThreshold: 1.0, evict: true);

foreach (var r in grad)
    Console.WriteLine($"  [{(r.Graduated ? "GRADUATE→evict" : "keep")}] score-without-lesson {r.ScoreWithoutLesson * 100:0}%  ·  {r.Warning}");

// measure the post-bake state: run the suite on the BAKED model with the (now-pruned) memory
{
    double q = 0;
    foreach (var t in tickers) { var d = await baked.GenerateAsync(Ctx(t), Array.Empty<Lesson>(), null, null, 0, default); q += baked.Evaluate(d, Ctx(t)).Score; }
    var quality = q / tickers.Length;
    var lessons = (await store.AllAsync()).Count(l => l.Agent == Agent && l.Trust != Trust.Quarantined);
    series.Add(new JsonObject { ["session"] = Sessions + 1, ["phase"] = "graduated", ["quality"] = Math.Round(quality, 3), ["calls"] = 1.0, ["lessons"] = lessons, ["ctxTokens"] = LessonTokens() });
    Console.WriteLine($"\n  post-bake: quality {quality * 100:0.0}%  ·  lessons in memory {lessons}  ·  ctx tokens {LessonTokens()}");
}

var seriesPath = Path.Combine(dir, "compounding.json");
File.WriteAllText(seriesPath, series.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"\ncompounding series → {seriesPath}");
Console.WriteLine("\n→ Fast loop: quality up, cost down. Slow loop: knowledge moves to weights, memory pruned to zero, quality held.");

static string Clip(string? s) => s is null ? "—" : (s.Length <= 88 ? s : s[..88] + "…");

// ─────────────────────────────────────────────────────────────────────────────
// Deterministic agent: writes a one-line note under 5 checks. It misses TWO constraints
// (risk caveat, conciseness) until it either (a) gets a lesson injected, (b) is told via
// the revise critique, or (c) is BAKED (knows them intrinsically — the fine-tuned model).
// Same reward-driven mechanics as the real harness; no model, so the arc is reproducible.
// ─────────────────────────────────────────────────────────────────────────────
sealed class NoteAgent : IAgent
{
    private readonly bool _baked;
    public NoteAgent(bool baked) => _baked = baked;
    public const string SystemPrompt = "You are an equity analyst writing a one-line investment note.";
    private const string Sector = "advisory";
    public string Id => "note-advisor";

    public Task<string> GenerateAsync(AgentContext ctx, IReadOnlyList<Lesson> lessons, string? critique, string? prior, int attempt, CancellationToken ct)
    {
        string Has(string kw) => lessons.Any(l => l.Warning.ToLowerInvariant().Contains(kw)) || (critique?.ToLowerInvariant().Contains(kw) ?? false) ? kw : "";
        var knowsRisk    = _baked || Has("risk") != "";
        var knowsConcise = _baked || Has("concise") != "" || Has("under 30") != "" || Has("one sentence") != "";

        var note = $"{ctx.Ticker}: Buy because fundamentals are strong";
        note += knowsRisk ? ", though key risks remain." : ".";
        if (!knowsConcise) note += " This view reflects a broadly favorable outlook across many considerations and numerous additional qualifying clauses appended here purely to exceed the concise word budget by a wide margin indeed.";
        return Task.FromResult(note);
    }

    public Reward Evaluate(string draft, AgentContext ctx) => Grade(draft, ctx.Ticker);

    public static Reward Grade(string note, string ticker)
    {
        var t = (note ?? "").ToLowerInvariant();
        var words = t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        return Checks.Of(
            ("names the ticker",       t.Contains(ticker.ToLowerInvariant())),
            ("states a buy/hold/sell", new[] { "buy", "hold", "sell" }.Any(t.Contains)),
            ("gives a reason",         t.Contains("because")),
            ("includes a risk note",   t.Contains("risk")),
            ("is concise (≤30 words)", words > 0 && words <= 30));
    }

    public Lesson? LessonFor(string trigger, AgentContext ctx)
    {
        var warning = trigger switch
        {
            "includes a risk note"   => "Always include a brief risk caveat (mention risk).",
            "is concise (≤30 words)" => "Keep the note to one concise sentence, under 30 words.",
            _ => null,
        };
        return warning is null ? null : new Lesson
        {
            Id = $"{Id}|{Sector}|{trigger}", Agent = Id, Sector = Sector, Trigger = trigger,
            Condition = "writing a one-line investment note", Warning = warning, Type = LessonType.Strategy,
        };
    }
}
