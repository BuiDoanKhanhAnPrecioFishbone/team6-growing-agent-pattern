using AIAssistant.Harness;

// ─────────────────────────────────────────────────────────────────────────────
// ctxdemo — context management, live. A "needle in a haystack": plant a fact early
// in a long session, bury it under filler, then ask about it at the end — under a
// tight token budget. Two strategies at the SAME budget:
//   (1) TRUNCATE  — keep only the last few turns (what a naive window does). The fact is gone.
//   (2) COMPACT   — Context.FitAsync summarizes the older turns (keeping the fact) + recent turns.
// Same cheap model, same budget; compaction remembers, truncation forgets.
// Live-only: set AGENT_LLM_* first (see docs/FOUNDRY-SETUP.md).
// ─────────────────────────────────────────────────────────────────────────────

if (!ToolLoop.Enabled)
{
    Console.WriteLine("""
        ctxdemo needs a live model. Set your Foundry deployment, then re-run:
          $env:AGENT_LLM_BASE_URL = "https://<resource>.openai.azure.com/openai/v1"
          $env:AGENT_LLM_API_KEY  = "<key>"
          $env:AGENT_LLM_MODEL    = "gpt-4.1-mini"
          dotnet run --project ctxdemo
        """);
    return;
}

// Build a long session: a needle up front, lots of filler, the question at the end.
var turns = new List<ChatTurn>
{
    new("system", "You are a client-advisory assistant with a long memory. Answer concisely from the conversation."),
    new("user", "Onboarding notes to remember: the client is Mr. Halvorsen; his target annual return is 12%; his risk tolerance is LOW; he will not hold tobacco or gambling stocks."),
    new("assistant", "Noted — recorded Mr. Halvorsen's mandate: 12% target return, LOW risk tolerance, no tobacco or gambling."),
};
string[] filler =
{
    "What time zone is the Oslo office in?", "Please switch all figures to USD.",
    "Summarize what an index fund is.", "What's the difference between a stock and a bond?",
    "Explain dollar-cost averaging.", "How does compound interest work?",
    "What is an ETF expense ratio?", "Define market capitalization.",
    "What is a dividend yield?", "Explain the price-to-earnings ratio.",
    "What is diversification?", "How do I read a balance sheet?",
};
foreach (var q in filler) { turns.Add(new("user", q)); turns.Add(new("assistant", $"(Here is a brief explanation of: {q})")); }
turns.Add(new("user", "Now, for the portfolio memo: what is the client's target annual return, and what is his risk tolerance?"));

var budget = new ContextBudget(MaxTokens: 220, KeepRecent: 4);
bool Recalled(string a) { a = a.ToLowerInvariant(); return a.Contains("12") && a.Contains("low"); }

// (1) naive truncation at the same budget: system + last KeepRecent turns (older turns, incl. the needle, dropped)
var sys = turns[0];
var truncated = new List<ChatTurn> { sys };
truncated.AddRange(turns.Skip(1).TakeLast(budget.KeepRecent));

// (2) compaction: summarize the middle (LLM), keep system + recent
var compacted = await Context.FitAsync(turns, budget, Context.LlmSummarizer());

Console.WriteLine($"session: {turns.Count} turns (~{Context.EstimateTokens(turns)} tok), budget {budget.MaxTokens} tok, keepRecent {budget.KeepRecent}\n");

var aTrunc = await ToolLoop.CompleteMessagesAsync(truncated);
var aComp = await ToolLoop.CompleteMessagesAsync(compacted);

void Report(string label, List<ChatTurn> sent, string answer) => Console.WriteLine(
    $"[{label}]  sent {sent.Count} turns / ~{Context.EstimateTokens(sent)} tok  ·  recalled the mandate: {(Recalled(answer) ? "YES ✓" : "NO ✗")}\n   → {answer.Replace('\n', ' ').Trim()}\n");

Report("TRUNCATE", truncated, aTrunc);
Report("COMPACT ", compacted, aComp);

Console.WriteLine(Recalled(aComp) && !Recalled(aTrunc)
    ? "→ Same model, same budget: compaction remembered the client mandate; naive truncation forgot it."
    : "→ (Re-run: with a live model, compaction should recall the mandate while truncation drops it.)");
