using AIAssistant.Harness;

// ─────────────────────────────────────────────────────────────────────────────
// memcon — the CONSOLIDATION proof. A growing agent accumulates many RELATED lessons
// (same theme, different wording). Left alone, memory grows linearly with experience
// and retrieval blurs. ConsolidateAsync clusters the related ones and distils each
// cluster into ONE meta-lesson — so memory summarizes itself and injected context
// stays small at scale. Deterministic & offline (preset embeddings, digest fallback).
//
//   5 related "risk-disclosure" lessons + 1 unrelated "valuation" lesson
//     → consolidate → 1 meta-lesson (folds the 5) + the unrelated one, untouched
// ─────────────────────────────────────────────────────────────────────────────

int pass = 0, fail = 0;
void Check(string name, bool ok) { Console.WriteLine($"   [{(ok ? "PASS" : "FAIL")}] {name}"); if (ok) pass++; else fail++; }

// Craft embeddings so the 5 are pairwise-related (cosine ≈ 0.86: clusters, but below the 0.92 dedup line),
// and the 6th is orthogonal (cosine 0: never folded). Shared component at index 0 + a distinct small axis.
static float[] Related(int i, int dim = 8) { var v = new float[dim]; v[0] = 1f; v[i] = 0.4f; return v; }
static float[] Ortho(int i, int dim = 8) { var v = new float[dim]; v[i] = 1f; return v; }

const string Agent = "advisor", Sector = "advisory";
var path = Path.Combine(Path.GetTempPath(), "memcon.json"); File.Delete(path);
var store = new SemanticLessonStore(path);

Lesson L(string trig, float[] emb, string warning) => new()
{
    Id = $"{Agent}|{Sector}|{trig}", Agent = Agent, Sector = Sector, Trigger = trig,
    Condition = "writing a recommendation", Warning = warning, Embedding = emb,
    Date = "2026-08-01", LastUsed = "2026-08-01",
};

Console.WriteLine("memcon — does memory summarize itself at scale?\n");
Console.WriteLine($"model: {(ToolLoop.Enabled ? "live (LLM distillation)" : "offline (deterministic digest)")}\n");

// 5 related lessons about disclosing risk, worded differently…
var related = new[]
{
    ("risk-1", "Always disclose material risks in a recommendation."),
    ("risk-2", "Mention downside scenarios before recommending a buy."),
    ("risk-3", "Note key risk factors so the client sees both sides."),
    ("risk-4", "Never omit the main risks when giving a call."),
    ("risk-5", "State at least one concrete risk to the thesis."),
};
for (var i = 0; i < related.Length; i++)
    await store.WriteAsync(L(related[i].Item1, Related(i + 1), related[i].Item2));
// …and one unrelated lesson about valuation.
await store.WriteAsync(L("valuation", Ortho(7), "Anchor the call on valuation, not price momentum."));

// promote one member so we can check the meta inherits the strongest trust
await store.PromoteAsync($"{Agent}|{Sector}|risk-3");

var before = (await store.AllAsync()).Count(l => l.Agent == Agent);
Console.WriteLine($"before: {before} lessons ({related.Length} related risk rules + 1 valuation)");
Check("all 6 stored (related, but none dedup-merged)", before == 6);

// ── consolidate ──
var folded = await store.ConsolidateAsync(Agent, minCluster: 3);
var after = (await store.AllAsync()).Where(l => l.Agent == Agent).ToList();

Console.WriteLine($"\nafter consolidate: {after.Count} lessons ({folded} cluster folded)\n");
foreach (var l in after) Console.WriteLine($"   • [{l.Trust}] ({l.Trigger}) {l.Warning}");

var meta = after.FirstOrDefault(l => l.Trigger == "consolidated");
Console.WriteLine();
Check("one cluster was folded", folded == 1);
Check("memory shrank 6 → 2", after.Count == 2);
Check("a meta-lesson was created", meta is not null);
Check("meta distils the 5 related rules", meta is not null && meta.LearnedFrom.Contains("consolidated 5"));
Check("meta inherits the strongest trust (Verified, from risk-3)", meta?.Trust == Trust.Verified);
Check("meta stays injectable (short, no injection markers)", meta is not null && meta.Warning.Length <= 400);
Check("the unrelated valuation lesson is untouched", after.Any(l => l.Trigger == "valuation"));

// retrieval still surfaces the guidance — now as one compact meta-rule
var got = await store.RetrieveAsync(Agent, new AgentFeatures(Sector, Array.Empty<string>(), "writing a recommendation with risks"), 3, default);
Check("retrieval still returns risk guidance after consolidation",
    got.Any(l => l.Trigger == "consolidated" || l.Warning.ToLowerInvariant().Contains("risk")));

Console.WriteLine($"\n{pass} passed, {fail} failed.");
Console.WriteLine(fail == 0
    ? "\nVerdict: related lessons fold into one meta-rule — memory summarizes itself, context stays small at scale."
    : "\nVerdict: consolidation regressed — investigate.");
Environment.Exit(fail == 0 ? 0 : 1);
