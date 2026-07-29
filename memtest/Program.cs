using AIAssistant.Harness;

// Verify Memory v2 D3-4: vector shortlist + LLM recall + two-phase. The key test is DISAMBIGUATION —
// two lessons are lexically similar ("cite ... source ...") but only one APPLIES. Vector ranks by
// similarity; the LLM recall should pick by applicability. Runs offline (vector only) or live (recall).
var path = Path.Combine(Path.GetTempPath(), "memtest-lessons.json");
if (File.Exists(path)) File.Delete(path);
var store = new SemanticLessonStore(path);

async Task Add(string id, string cond, string warning) =>
    await store.WriteAsync(new Lesson { Id = id, Agent = "A", Sector = "x", Date = "2026-07-29", Condition = cond, Warning = warning });

await Add("cite-evidence", "moat evidence sources",   "Cite only the provided sources for moat evidence; never invent a citation or page.");
await Add("cite-price",    "valuation price source",  "The valuation price figure must carry its as-of source and date.");
await Add("redflag",       "financial health verdict","Always surface every fired red flag such as a multi-year asset-turnover decline.");
await Add("assume",        "valuation assumptions",   "Type every valuation assumption by basis: computed, cited, or human_override.");
await Add("disclaim",      "buy recommendation",      "Every buy recommendation must include the educational, not-advice disclaimer.");
await Add("alert",         "monitoring alerts",       "Every monitoring alert must cite the statement period it came from.");

Console.WriteLine($"stored 6 lessons.  recall enabled: {Recall.Enabled}\n");

async Task Query(string situation, int k)
{
    var res = await store.RetrieveAsync("A", new AgentFeatures("x", Array.Empty<string>(), situation), topK: k);
    Console.WriteLine($"SITUATION: {situation}");
    Console.WriteLine("  → " + (res.Count == 0 ? "(none)" : string.Join(", ", res.Select(l => l.Id))));
    Console.WriteLine();
}

// Only cite-evidence applies — but cite-price is lexically similar. Recall should drop cite-price.
await Query("the moat evidence cited a source that was not in the provided list", 2);
await Query("the valuation emitted an assumption with an empty basis field", 2);
await Query("a buy was recommended without the disclaimer", 2);
