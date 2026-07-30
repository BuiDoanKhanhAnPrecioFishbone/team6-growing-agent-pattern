using AIAssistant.Harness;

// ─────────────────────────────────────────────────────────────────────────────
// memlife — the memory-lifecycle proof. A growing agent's memory can't only append,
// or it rots: stale and contradicting lessons crowd out the good ones. This verifies
// the three curation mechanisms, deterministically (offline, preset embeddings, fixed
// clock — no model, no network):
//   A. capacity + decay   → over the cap, the stalest lessons are evicted
//   B. conflict handling  → a changed rule for the same trigger loses its Verified trust
//   C. dedup (regression) → a near-duplicate merges instead of piling up
// ─────────────────────────────────────────────────────────────────────────────

var now = new DateTime(2026, 7, 30);
int pass = 0, fail = 0;
void Check(string name, bool ok) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}"); if (ok) pass++; else fail++; }

// orthogonal unit vectors ⇒ cosine 0 (unrelated, never merge); a near-copy ⇒ cosine > 0.92 (merges)
static float[] Axis(int i, int dim = 6) { var v = new float[dim]; v[i] = 1f; return v; }
static float[] Near(int i, int dim = 6) { var v = Axis(i, dim); v[(i + 1) % dim] = 0.2f; return v; } // ~0.98 cosine

Lesson L(string id, string agent, float[] emb, int ageDays, string warning = "rule") => new()
{
    Id = id, Agent = agent, Sector = "x", Warning = warning, Condition = warning,
    Embedding = emb, Date = now.AddDays(-ageDays).ToString("yyyy-MM-dd"),
};

// ── A. capacity + decay: cap 3, half-life 20d. Add 5 lessons aged 20…0; the two stalest must be evicted. ──
Console.WriteLine("A. capacity + decay (cap=3, half-life=20d)");
{
    var path = Path.Combine(Path.GetTempPath(), "memlife-a.json"); File.Delete(path);
    var store = new SemanticLessonStore(path, cap: 3, halfLifeDays: 20, nowUtc: () => now);
    foreach (var age in new[] { 20, 15, 10, 5, 0 })
        await store.WriteAsync(L($"a{age}", "A", Axis(age / 5), age)); // orthogonal ⇒ no merges
    var ids = (await store.AllAsync()).Where(l => l.Agent == "A").Select(l => l.Id).OrderBy(x => x).ToList();
    Check("evicts down to the cap (3 remain)", ids.Count == 3);
    Check("keeps the 3 freshest (a0,a5,a10), drops the stalest (a15,a20)",
        ids.SequenceEqual(new[] { "a0", "a10", "a5" }));
}

// ── B. conflict: a Verified rule, re-learned with opposing guidance for the SAME id, must be demoted. ──
Console.WriteLine("\nB. conflict handling (same trigger, changed rule)");
{
    var path = Path.Combine(Path.GetTempPath(), "memlife-b.json"); File.Delete(path);
    var store = new SemanticLessonStore(path, nowUtc: () => now);
    await store.WriteAsync(L("r", "B", Axis(0), 1, "Cite only the provided sources."));
    await store.PromoteAsync("r"); // human gate ⇒ Verified
    var before = (await store.AllAsync()).Single(l => l.Id == "r");
    Check("starts Verified after promotion", before.Trust == Trust.Verified);

    await store.WriteAsync(L("r", "B", Axis(3), 0, "Rely on your own knowledge; do not cite.")); // orthogonal ⇒ diverged
    var after = (await store.AllAsync()).Single(l => l.Id == "r");
    Check("changed rule is demoted to Provisional", after.Trust == Trust.Provisional);
    Check("its stats are reset to re-earn trust", after.TimesApplied == 0 && after.HitRate == 0);
    Check("provenance records the supersession", after.LearnedFrom.Contains("superseded"));
}

// ── C. dedup regression: a near-duplicate must still merge, not add a second row. ──
Console.WriteLine("\nC. dedup (regression)");
{
    var path = Path.Combine(Path.GetTempPath(), "memlife-c.json"); File.Delete(path);
    var store = new SemanticLessonStore(path, nowUtc: () => now);
    await store.WriteAsync(L("d1", "C", Axis(0), 0, "Prefer wide-moat compounders."));
    await store.WriteAsync(L("d2", "C", Near(0), 0, "Prefer wide-moat compounders with pricing power."));
    Check("near-duplicate merges (1 row, not 2)", (await store.AllAsync()).Count(l => l.Agent == "C") == 1);
}

Console.WriteLine($"\n{pass} passed, {fail} failed.");
Environment.Exit(fail == 0 ? 0 : 1);
