using System.Text.Json.Nodes;
using AIAssistant.Harness;

// ─────────────────────────────────────────────────────────────────────────────
// flywheel — the ART data flywheel. We don't need training to ship (the fast loop
// already grows the agent), but EVERY reward-labeled run is, for free, a dataset a
// trainer can use later. This runs the real harness on a toy "learns to cite" agent
// (offline, deterministic — no model needed) and exports the same run three ways:
//   sft.jsonl        — winning completions (imitation / SFT)
//   preference.jsonl — chosen vs rejected (DPO / reward model)
//   rl.jsonl         — every attempt + its scalar reward (GRPO / ART)
// ─────────────────────────────────────────────────────────────────────────────

var topics = new[] { "vector search", "the reward loop", "semantic memory", "tool grounding", "context compaction" };

var store = new SemanticLessonStore(Path.Combine(Path.GetTempPath(), "flywheel-lessons.json"));
store.Clear();
var harness = new AgentHarness(store);
var opt = new HarnessOptions(MaxIters: 1, Threshold: 1.0, RetrieveTopK: 3, Samples: 2); // best-of-2 → a good and a bad draft each run

var sft = new List<string>(); var pref = new List<string>(); var rl = new List<string>();
foreach (var topic in topics)
{
    var task = $"Summarize \"{topic}\" in one sentence and cite a source in [brackets].";
    var ctx = new AgentContext
    {
        Ticker = "T",
        Features = new AgentFeatures("demo", Array.Empty<string>(), task),
        Input = new JsonObject { ["task"] = task },
        AllowedSources = Array.Empty<string>(),
    };
    var o = await harness.RunAsync(new CiteAgent(topic), ctx, opt, default);
    if (TrainingExporter.Sft(task, o) is { } s) sft.Add(s);
    if (TrainingExporter.Preference(task, o) is { } p) pref.Add(p);
    rl.AddRange(TrainingExporter.Rl(task, o));
}

var dir = Path.Combine(Path.GetTempPath(), "flywheel-dataset");
Directory.CreateDirectory(dir);
File.WriteAllLines(Path.Combine(dir, "sft.jsonl"), sft);
File.WriteAllLines(Path.Combine(dir, "preference.jsonl"), pref);
File.WriteAllLines(Path.Combine(dir, "rl.jsonl"), rl);

Console.WriteLine($"flywheel — {topics.Length} harness runs → a labeled training corpus (offline, deterministic):\n");
Console.WriteLine($"  sft.jsonl         {sft.Count,2} samples   winning completions        (SFT / imitation)");
Console.WriteLine($"  preference.jsonl  {pref.Count,2} pairs     chosen vs rejected          (DPO / reward model)");
Console.WriteLine($"  rl.jsonl          {rl.Count,2} samples   every attempt + reward      (GRPO / ART)");
Console.WriteLine($"\n  written to {dir}\n");
Console.WriteLine("  sample SFT line:        " + Clip(sft.FirstOrDefault()));
Console.WriteLine("  sample preference line: " + Clip(pref.FirstOrDefault()));
Console.WriteLine("  sample RL line:         " + Clip(rl.FirstOrDefault()));
Console.WriteLine("\n→ The fast loop ships the agent today; the same runs make it training-ready for tomorrow — no GPU now.");

static string Clip(string? s) => s is null ? "—" : (s.Length <= 96 ? s : s[..96] + "…");

// A toy agent that learns to cite: best-of-2 yields one uncited (fail) and one cited (pass) draft per run,
// so every run produces a reward gap → real SFT + preference + RL rows. Deterministic; no model.
sealed class CiteAgent : IAgent
{
    private readonly string _t; public CiteAgent(string t) => _t = t;
    public string Id => "flywheel-cite";
    public Task<string> GenerateAsync(AgentContext ctx, IReadOnlyList<Lesson> lessons, string? critique, string? prior, int attempt, CancellationToken ct)
    {
        var cited = attempt % 2 == 1; // even sample = uncited draft, odd sample = cited draft
        var s = cited
            ? $"{_t} is a technique in the growing-agent harness that improves results [source: PATTERN.md]."
            : $"{_t} is a technique in the growing-agent harness that improves results.";
        return Task.FromResult(s);
    }
    public Reward Evaluate(string draft, AgentContext ctx)
    {
        var ok = draft.Contains("[source");
        return new Reward(ok, ok ? 1.0 : 0.3, new Dictionary<string, double> { ["cited"] = ok ? 1 : 0 },
            ok ? new HashSet<string>() : new HashSet<string> { "UNCITED" }, ok ? "" : "Cite a source in [brackets].");
    }
    public Lesson? LessonFor(string trigger, AgentContext ctx) => new Lesson
    {
        Id = "flywheel-cite|demo|UNCITED", Agent = "flywheel-cite", Sector = "demo", Trigger = "UNCITED",
        Condition = "writing a summary", Warning = "Always cite a source in [brackets].",
    };
}
