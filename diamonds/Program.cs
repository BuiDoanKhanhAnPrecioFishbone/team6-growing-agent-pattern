using System.Text.Json.Nodes;
using AIAssistant.Harness;

// ─────────────────────────────────────────────────────────────────────────────
// diamonds — the free-data thesis, made visible. "Coal into diamonds": ordinary usage
// is the coal; the reward loop is the press; verified lessons + labeled examples are the
// diamonds. This runs the harness on a suite and shows, offline & deterministically:
//   A1 · the DATA-VALUE meter   — lessons + labeled examples mined ≈ labeling $ avoided
//   A2 · "show one diamond"      — for each learned lesson, the answer WITHOUT it vs WITH it
// Emits diamonds.json for the artifact.
// ─────────────────────────────────────────────────────────────────────────────

const string Agent = "note-advisor", Sector = "advisory";
var tickers = new[] { "VNM", "FPT", "HPG" };

var storePath = Path.Combine(Path.GetTempPath(), "diamonds.json"); File.Delete(storePath);
var store = new SemanticLessonStore(storePath);
var harness = new AgentHarness(store);
var opt = new HarnessOptions(MaxIters: 3, Threshold: 1.0, RetrieveTopK: 3, Samples: 1);

AgentContext Ctx(string t) => new()
{
    Ticker = t,
    Features = new AgentFeatures(Sector, Array.Empty<string>(), $"one-line investment note for {t}"),
    Input = new JsonObject { ["ticker"] = t },
    AllowedSources = Array.Empty<string>(),
};

// ── run the suite → learn lessons → export training data ──
int sft = 0, pref = 0, rl = 0;
foreach (var t in tickers)
{
    var o = await harness.RunAsync(new NoteAgent(), Ctx(t), opt, default);
    var task = $"Ticker: {t}. Write the note.";
    if (TrainingExporter.Sft(task, o) is not null) sft++;
    if (TrainingExporter.Preference(task, o) is not null) pref++;
    rl += (o.Attempts?.Count ?? 0);
}
var lessons = (await store.AllAsync()).Where(l => l.Agent == Agent).ToList();
var value = DataValue.Estimate(lessons, sft, pref, rl);

Console.WriteLine("diamonds — turning ordinary usage into training data\n");
Console.WriteLine("A1 · DATA-VALUE METER (mined for free from " + tickers.Length + " runs)");
Console.WriteLine($"   verified lessons   : {value.VerifiedLessons}");
Console.WriteLine($"   labeled examples   : {value.LabeledExamples}   (SFT {value.SftExamples} · pref {value.PreferencePairs} · RL {value.RlSamples})");
Console.WriteLine($"   ≈ labeling avoided : ${value.DollarsAvoided:0.00}   (at ${value.PricePerExample:0.00}/example)");
Console.WriteLine($"   → {DataValue.Line(value)}\n");

// ── A2 · show each diamond: the answer WITHOUT this one lesson vs WITH it ──
Console.WriteLine("A2 · THE DIAMONDS (each lesson's before → after, on the same task)\n");
var learned = lessons.Where(l => l.Trigger is "includes a risk note" or "is concise (≤30 words)").ToList();
var diamondsJson = new JsonArray();
var demoT = tickers[0];
foreach (var L in learned)
{
    var others = learned.Where(x => x.Id != L.Id).ToList();     // memory WITHOUT this lesson
    var before = await new NoteAgent().GenerateAsync(Ctx(demoT), others, null, null, 0, default);
    var after = await new NoteAgent().GenerateAsync(Ctx(demoT), learned, null, null, 0, default);
    var bs = NoteAgent.Grade(before, demoT); var as_ = NoteAgent.Grade(after, demoT);
    Console.WriteLine($"   💎 {L.Warning}");
    Console.WriteLine($"      without → {bs.Score * 100,3:0}%  {Trim(before)}");
    Console.WriteLine($"      with    → {as_.Score * 100,3:0}%  {Trim(after)}\n");
    diamondsJson.Add(new JsonObject
    {
        ["lesson"] = L.Warning, ["condition"] = L.Condition, ["trust"] = L.Trust.ToString(),
        ["before"] = before, ["beforeScore"] = Math.Round(bs.Score, 2),
        ["after"] = after, ["afterScore"] = Math.Round(as_.Score, 2),
    });
}

// headline: the whole memory's effect vs bare
var bareHead = NoteAgent.Grade(await new NoteAgent().GenerateAsync(Ctx(demoT), Array.Empty<Lesson>(), null, null, 0, default), demoT);
var fullHead = NoteAgent.Grade(await new NoteAgent().GenerateAsync(Ctx(demoT), learned, null, null, 0, default), demoT);

var outJson = new JsonObject
{
    ["headline"] = new JsonObject { ["ticker"] = demoT, ["bareScore"] = Math.Round(bareHead.Score, 2), ["memoryScore"] = Math.Round(fullHead.Score, 2) },
    ["dataValue"] = new JsonObject
    {
        ["verifiedLessons"] = value.VerifiedLessons, ["labeledExamples"] = value.LabeledExamples,
        ["sft"] = value.SftExamples, ["preference"] = value.PreferencePairs, ["rl"] = value.RlSamples,
        ["pricePerExample"] = value.PricePerExample, ["dollarsAvoided"] = value.DollarsAvoided,
    },
    ["diamonds"] = diamondsJson,
};
var dir = Path.Combine(Path.GetTempPath(), "diamonds-out"); Directory.CreateDirectory(dir);
var outPath = Path.Combine(dir, "diamonds.json");
File.WriteAllText(outPath, outJson.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"headline: bare {bareHead.Score * 100:0}% → with memory {fullHead.Score * 100:0}%  ·  series → {outPath}");
Console.WriteLine("\n→ No user research, no labeling budget: the reward labels every step of ordinary use. Coal → diamonds.");

static string Trim(string s) => s.Length <= 62 ? s : s[..62] + "…";

// Deterministic agent (same shape as slowloop): misses the risk caveat + conciseness until a lesson or the
// revise critique supplies them — so learning, and each lesson's marginal lift, is reproducible offline.
sealed class NoteAgent : IAgent
{
    private const string Sector = "advisory";
    public string Id => "note-advisor";

    public Task<string> GenerateAsync(AgentContext ctx, IReadOnlyList<Lesson> lessons, string? critique, string? prior, int attempt, CancellationToken ct)
    {
        bool Has(string kw) => lessons.Any(l => l.Warning.ToLowerInvariant().Contains(kw)) || (critique?.ToLowerInvariant().Contains(kw) ?? false);
        var risk = Has("risk");
        var concise = Has("concise") || Has("under 30") || Has("one sentence");
        var note = $"{ctx.Ticker}: Buy because fundamentals are strong";
        note += risk ? ", though key risks remain." : ".";
        if (!concise) note += " This reflects a broadly favorable outlook across many considerations and numerous extra qualifying clauses appended purely to exceed the concise word budget by a wide margin indeed.";
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
