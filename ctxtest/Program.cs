using AIAssistant.Harness;
using System.Text.Json.Nodes;

// ─────────────────────────────────────────────────────────────────────────────
// ctxtest — the context-management proof. A long session must stay within a token
// budget or it overflows the window (and cost climbs). This verifies both mechanisms
// deterministically (offline, no model, deterministic digest):
//   A. FitAsync            — a long conversation compacts to fit, keeping system + recent + a summary
//   B. CompactToolHistory  — a tool-loop history stays bounded, structure (tool pairing) intact
// ─────────────────────────────────────────────────────────────────────────────

int pass = 0, fail = 0;
void Check(string name, bool ok) { Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}"); if (ok) pass++; else fail++; }

// ── A. conversation compaction ──
Console.WriteLine("A. FitAsync — compact a long conversation to a token budget");
{
    var turns = new List<ChatTurn> { new("system", "You are a value-investing research assistant.") };
    for (var i = 1; i <= 30; i++)
        turns.Add(new(i % 2 == 1 ? "user" : "assistant",
            $"Turn {i}: " + string.Concat(Enumerable.Repeat($"detail-{i} ", 20)))); // ~140 chars each

    var before = Context.EstimateTokens(turns);
    var budget = new ContextBudget(MaxTokens: 200, KeepRecent: 4);
    var fitted = await Context.FitAsync(turns, budget); // no summarizer ⇒ deterministic digest
    var after = Context.EstimateTokens(fitted);

    Console.WriteLine($"     {turns.Count} turns / ~{before} tok  →  {fitted.Count} turns / ~{after} tok");
    Check("shrinks the context (fewer tokens than before)", after < before);
    Check("keeps the system message first", fitted[0].Role == "system" && fitted[0].Content.StartsWith("You are"));
    Check("inserts exactly one compaction summary", fitted.Count(t => t.Content.StartsWith("Summary of the earlier")) == 1);
    Check("preserves the last 4 turns verbatim", fitted.TakeLast(4).SequenceEqual(turns.TakeLast(4)));
    Check("summary retains the gist of older turns (mentions an early turn)", fitted.Any(t => t.Content.Contains("Turn 3")));
    var underBudget = await Context.FitAsync(turns.Take(3).ToList(), budget);
    Check("a conversation already under budget is untouched", underBudget.Count == 3);
}

// ── B. tool-loop history compaction ──
Console.WriteLine("\nB. CompactToolHistory — bound a tool-loop's growing history in place");
{
    var big = string.Concat(Enumerable.Repeat("search-result-text ", 40)); // ~760 chars ≈ 190 tok each
    var history = new List<JsonNode>
    {
        new JsonObject { ["role"] = "system", ["content"] = "sys" },
        new JsonObject { ["role"] = "user", ["content"] = "find the CEO and the HQ city and the founding year" },
    };
    for (var i = 0; i < 5; i++) // five prior tool rounds, each a bulky result
    {
        history.Add(new JsonObject { ["role"] = "assistant", ["content"] = "", ["tool_calls"] = new JsonArray() });
        history.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = $"c{i}", ["content"] = big });
    }
    var before = history.Sum(m => Context.EstimateTokens((m["content"] as JsonValue)?.GetValue<string>()));
    Context.CompactToolHistory(history, new ContextBudget(MaxTokens: 300, KeepRecent: 2));
    var after = history.Sum(m => Context.EstimateTokens((m["content"] as JsonValue)?.GetValue<string>()));

    Console.WriteLine($"     ~{before} tok  →  ~{after} tok  ({history.Count} messages, unchanged)");
    Check("total tokens drop under the budget", after <= 300);
    Check("message count is unchanged (structure preserved)", history.Count == 12);
    Check("every tool message still has its tool_call_id (pairing intact)",
        history.Where(m => (m["role"] as JsonValue)?.GetValue<string>() == "tool").All(m => m["tool_call_id"] is not null));
    Check("the most recent tool result is kept verbatim",
        (history[^1]["content"] as JsonValue)?.GetValue<string>() == big);
    Check("an older tool result was trimmed",
        history.Any(m => (m["content"] as JsonValue)?.GetValue<string>() == "[older tool result trimmed to save context]"));
}

Console.WriteLine($"\n{pass} passed, {fail} failed.");
Environment.Exit(fail == 0 ? 0 : 1);
