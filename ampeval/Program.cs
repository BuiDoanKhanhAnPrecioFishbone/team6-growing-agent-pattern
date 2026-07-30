using AIAssistant.Harness;

// ─────────────────────────────────────────────────────────────────────────────
// ampeval — the amplifier proof. Same cheap model, two modes, measured:
//   (1) BARE      — one completion, no tools. What gpt-4.1-mini knows on its own.
//   (2) +WEB      — the SAME model with the harness's read-only web_search tool.
// Grounding is the antidote to a cheap model's #1 failure: confidently wrong facts.
// Live-only: set AGENT_LLM_* to your Foundry deployment first (see docs/FOUNDRY-SETUP.md).
// ─────────────────────────────────────────────────────────────────────────────

if (!ToolLoop.Enabled)
{
    Console.WriteLine("""
        ampeval needs a live model. Set your Foundry deployment, then re-run:

          $env:AGENT_LLM_BASE_URL = "https://<resource>.openai.azure.com/openai/v1"
          $env:AGENT_LLM_API_KEY  = "<key>"
          $env:AGENT_LLM_MODEL    = "gpt-4.1-mini"
          dotnet run --project ampeval

        (web_search is keyless — it uses Wikipedia, no extra secret.)
        """);
    return;
}

// Facts that are verifiable on Wikipedia and that a small model tends to miss — a classic
// geography flub, a post-training-cutoff change, and a few details cheap models hallucinate.
var suite = new (string Q, string[] Accept)[]
{
    ("What is the capital city of Australia?",                                   new[] { "canberra" }),
    ("Who became the chief executive officer (CEO) of Berkshire Hathaway in 2026?", new[] { "abel" }),
    ("Who is the author of the investing book 'The Intelligent Investor'?",       new[] { "graham" }),
    ("In which U.S. city will the 2028 Summer Olympic Games be held?",            new[] { "los angeles" }),
    ("How many natural moons does the planet Mars have?",                         new[] { " two", "2" }),
    ("What is the largest planet in the Solar System?",                           new[] { "jupiter" }),
};

const string BareSys    = "You are a knowledgeable assistant. Answer in ONE short sentence. If you are not fully sure, give your single best guess — do not refuse and do not hedge.";
const string GroundedSys = "You are a knowledgeable assistant. You MUST call web_search to verify the facts BEFORE you answer, even if you think you know. Then answer in ONE short sentence.";

bool Hit(string answer, string[] accept)
{
    var a = answer.ToLowerInvariant();
    return accept.Any(k => a.Contains(k.Trim().ToLowerInvariant()));
}
static string Clip(string s) => (s = s.Replace('\n', ' ').Trim()).Length <= 68 ? s : s[..68] + "…";

Console.WriteLine($"ampeval · model={Environment.GetEnvironmentVariable("AGENT_LLM_MODEL")} · web_search=Wikipedia (keyless)\n");

int bareOk = 0, groundOk = 0, searches = 0;
Console.WriteLine($"{"question",-52} {"bare",-6} {"+web",-6}");
Console.WriteLine(new string('─', 66));

foreach (var (q, accept) in suite)
{
    var bare = await Safe(() => ToolLoop.CompleteAsync(BareSys, q));
    var bHit = Hit(bare, accept); if (bHit) bareOk++;

    var used = 0;
    var grounded = await Safe(() => ToolLoop.RunAsync(
        GroundedSys, q, new ITool[] { new WebSearchTool() },
        onCall: (name, _) => { if (name == "web_search") { used++; searches++; } }, maxSteps: 4));
    var gHit = Hit(grounded, accept); if (gHit) groundOk++;

    Console.WriteLine($"{Clip(q),-52} {(bHit ? "  ✓" : "  ✗"),-6} {(gHit ? "  ✓" : "  ✗"),-6}");
    Console.WriteLine($"    bare: {Clip(bare)}");
    Console.WriteLine($"    +web: {Clip(grounded)}  [{used} search]");
}

Console.WriteLine(new string('─', 66));
Console.WriteLine($"{"accuracy",-52} {$"{bareOk}/{suite.Length}",-6} {$"{groundOk}/{suite.Length}",-6}");
Console.WriteLine($"{"cost (model calls, approx)",-52} {"1×",-6} {"~2×",-6}");
Console.WriteLine($"web_search calls used: {searches}\n");
Console.WriteLine(groundOk > bareOk
    ? $"→ grounding turned {groundOk - bareOk} confidently-wrong answer(s) right — same cheap model, +web_search."
    : "→ no lift on this suite (the bare model already knew these); try harder/post-cutoff facts.");

static async Task<string> Safe(Func<Task<string>> f)
{
    try { return await f(); } catch (Exception e) { return "(error: " + e.Message + ")"; }
}
