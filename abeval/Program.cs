using AIAssistant.Harness;

// A/B: retrieval precision vs memory size. Both stores hold the SAME lessons (4 real ones + growing
// distractor noise). For each test situation we ask each store for the top-K lessons and check whether the
// APPLICABLE one is among them. Exact-match (v1) can't use the situation → precision decays as noise grows;
// semantic (v2) retrieves by meaning → holds. Offline (hash embeddings), deterministic, seeded.

const string AGENT = "analyst", SECTOR = "vn";
const int TOPK = 2;
var rng = new Random(42);

// 4 real categories: a canonical lesson + paraphrased situations that should retrieve it (not exact text).
var cats = new[]
{
    (Id: "L_cite",  Cond: "cite only the provided sources for evidence; never invent a citation",
        Tests: new[]{ "the moat draft used a source that was not in the provided list", "evidence quoted a page that was never given", "a citation was fabricated for the business summary" }),
    (Id: "L_flag",  Cond: "always surface every fired red flag in the health verdict",
        Tests: new[]{ "the financial verdict omitted the asset-turnover-decline flag", "a fired red flag was not reported to the reader", "the health summary hid a warning signal" }),
    (Id: "L_basis", Cond: "type every valuation assumption by basis: computed, cited, or human_override",
        Tests: new[]{ "an assumption was emitted with an empty basis field", "the discount rate had no basis label", "a growth assumption was not typed by basis" }),
    (Id: "L_disc",  Cond: "every buy recommendation must include the not-advice disclaimer",
        Tests: new[]{ "a buy call dropped the disclaimer text", "the recommendation was missing the not-advice note", "position sizing advice carried no disclaimer" }),
};

var distractorPool = new[]
{
    "prefer low-cost index funds for long-horizon retirement accounts","invalidate the cache when the upstream key rotates",
    "tokenize the input on whitespace before parsing","use tabs rather than spaces for indentation in this repo",
    "the quarterly board deck ships on the first monday","rotate API keys every ninety days for compliance",
    "compress images to webp before uploading to the CDN","the staging database resets every night at 2am",
    "prefer composition over inheritance for UI widgets","batch outbound emails to avoid rate limits",
    "the office wifi password changes each quarter","log timestamps in UTC across all services",
    "debounce the search box by 300 milliseconds","the mobile build needs a separate signing certificate",
    "archive tickets that have been closed for 90 days","use feature flags to dark-launch risky changes",
    "the vendor invoice is due net-30 from receipt","seed the demo tenant before every sales call",
    "rate-limit the public API to 100 requests per minute","the annual audit begins in the third week of january",
    "prefer server components for static content","memoize the expensive selector in the dashboard",
    "the changelog is generated from conventional commits","warm the cache on deploy to avoid a cold start",
    "the parking garage closes at 11pm on weekdays","use a bloom filter to pre-check membership cheaply",
};

async Task<double[]> PrecisionAt(int n)
{
    // Build the shared lesson set: 4 category lessons + (n-4) distractors, shuffled insertion order.
    var lessons = new List<Lesson>();
    foreach (var c in cats)
        lessons.Add(new Lesson { Id = c.Id, Agent = AGENT, Sector = SECTOR, Condition = c.Cond, Warning = c.Cond, Date = "2026-07-29" });
    for (var i = 0; i < n - cats.Length; i++)
    {
        var d = distractorPool[i % distractorPool.Length];
        lessons.Add(new Lesson { Id = $"D{i}", Agent = AGENT, Sector = SECTOR, Condition = d, Warning = d, Date = "2026-07-29" });
    }
    for (var i = lessons.Count - 1; i > 0; i--) { var j = rng.Next(i + 1); (lessons[i], lessons[j]) = (lessons[j], lessons[i]); }

    var v1Path = Path.Combine(Path.GetTempPath(), $"ab_v1_{n}.json");
    var v2Path = Path.Combine(Path.GetTempPath(), $"ab_v2_{n}.json");
    File.Delete(v1Path); File.Delete(v2Path);
    var v1 = new JsonLessonStore(v1Path);          // exact-match (situation ignored)
    var v2 = new SemanticLessonStore(v2Path, shortlist: 64); // semantic: hash shortlist → LLM recall (when AGENT_LLM_* set)
    foreach (var l in lessons) { await v1.WriteAsync(l); await v2.WriteAsync(l); }

    int hitsV1 = 0, hitsV2 = 0, total = 0;
    foreach (var c in cats)
        foreach (var t in c.Tests)
        {
            total++;
            var f = new AgentFeatures(SECTOR, Array.Empty<string>(), t);
            var r1 = await v1.RetrieveAsync(AGENT, f, TOPK);
            var r2 = await v2.RetrieveAsync(AGENT, f, TOPK);
            if (r1.Any(x => x.Id == c.Id)) hitsV1++;
            if (r2.Any(x => x.Id == c.Id)) hitsV2++;
        }
    return new[] { (double)hitsV1 / total, (double)hitsV2 / total };
}

Console.WriteLine("memorySize,exactMatch_v1,semantic_v2");
foreach (var n in new[] { 4, 8, 16, 32, 64, 128 })
{
    var p = await PrecisionAt(n);
    Console.WriteLine($"{n},{p[0]:0.000},{p[1]:0.000}");
}
