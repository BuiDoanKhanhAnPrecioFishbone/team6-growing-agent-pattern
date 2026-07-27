using System.Text.Json;
using System.Text.Json.Nodes;
using AIAssistant.Harness;

namespace AIAssistant.STemplate;

// ════════════════════════════════════════════════════════════════════════════
//  THE GROWING-AGENT TEMPLATE  —  implement THREE methods, get a learning agent.
//
//  Everything that makes the agent "grow" (the fast loop, the retrieve→revise→
//  write-lesson cycle, the self-correcting hit-rate memory) lives in the shared
//  harness. You supply only what is unique to YOUR step:
//
//    1) GenerateAsync — produce a draft (call your Foundry model, or a mock).
//    2) Evaluate      — THE REWARD. Hard gates + graded score + critique + the
//                       FailedTriggers that drive learning. This is the crux.
//    3) LessonFor     — turn a fixed mistake into a reusable, scoped lesson.
//
//  This file ships as a RUNNABLE toy (a "cite-your-answer" agent) so you start
//  green and watch the loop learn. Replace the toy bodies with your domain.
//  Reference implementation to copy from: agents/s2 (Moat).
// ════════════════════════════════════════════════════════════════════════════

public sealed class TemplateAgent : IAgent
{
    // Convention: "sN-name". Used as the memory partition key — keep it stable.
    public string Id => "sX-template";

    // ── TODO 1 · GENERATE ───────────────────────────────────────────────────
    // Produce a draft string (usually JSON). Inject `lessons` into your prompt so
    // past mistakes are avoided up front. On a revision, `critique` + `priorDraft`
    // come back — FIX, don't restart. Call your model via a ChatClient (see s2),
    // or return a deterministic mock so the agent runs offline.
    public Task<string> GenerateAsync(
        AgentContext ctx, IReadOnlyList<Lesson> lessons,
        string? critique, string? priorDraft, int attempt, CancellationToken ct)
    {
        var answer = $"{ctx.Ticker}: a grounded, specific answer for this step.";

        // Toy learnable behavior (mirrors s2): forget the citation on the very first
        // attempt of a fresh sector; get it right once a lesson is injected or on revision.
        var knowsToCite = critique is not null || lessons.Any(l => l.Trigger == "MISSING_CITATION");
        var citations = (knowsToCite || attempt > 0) && ctx.AllowedSources.Count > 0
            ? new JsonArray(ctx.AllowedSources.Select(s => (JsonNode?)s).ToArray())
            : new JsonArray();

        var draft = new JsonObject { ["answer"] = answer, ["citations"] = citations };
        return Task.FromResult(draft.ToJsonString());
    }

    // ── TODO 2 · EVALUATE (THE REWARD) ──────────────────────────────────────
    // Deterministic. Hard gates return score 0 (a failing draft must never rank
    // above a passing one). Graded components in [0,1] reward quality. Populate
    // FailedTriggers with stable keys — they name the mistakes the memory learns.
    public Reward Evaluate(string draft, AgentContext ctx)
    {
        var fails = new HashSet<string>();
        var critique = new List<string>();

        JsonObject? d = null;
        try { d = JsonNode.Parse(draft) as JsonObject; } catch { /* handled */ }

        // GATE: schema
        var answer = d?["answer"]?.GetValue<string>();
        if (d is null || string.IsNullOrWhiteSpace(answer))
        {
            fails.Add("SCHEMA");
            return Fail(fails, "GATE schema: return JSON { answer, citations[] } with a non-empty answer.");
        }

        // GATE: grounding — every claim needs a citation ("cite or drop")
        var citations = (d["citations"] as JsonArray) ?? new JsonArray();
        if (citations.Count == 0)
        {
            fails.Add("MISSING_CITATION");
            return Fail(fails, "GATE cite-or-drop: add at least one citation from the provided sources.");
        }

        // GRADED: specificity (toy — reward a longer, concrete answer)
        var specificity = Math.Min(1.0, answer!.Length / 60.0);
        if (specificity < 0.6) critique.Add("Specificity: make the answer more concrete (names, numbers).");

        var breakdown = new Dictionary<string, double> { ["specificity"] = Math.Round(specificity, 4) };
        var score = Math.Round(specificity, 4); // weights sum to 1 across your real components

        if (critique.Count == 0) critique.Add("Draft is grounded and specific — ready for the gate.");
        return new Reward(true, score, breakdown, fails, string.Join("\n", critique));
    }

    // ── TODO 3 · LEARN ──────────────────────────────────────────────────────
    // Map a fixed mistake (a trigger the loop cleared) to a SCOPED, CONDITIONAL
    // lesson. Keep the Id "{Id}|{sector}|{trigger}" so re-learning upserts, not
    // duplicates. Return null for triggers that make no useful guidance (e.g. SCHEMA).
    public Lesson? LessonFor(string trigger, AgentContext ctx)
    {
        var sector = ctx.Features.Sector;
        string? warning = trigger switch
        {
            "MISSING_CITATION" => $"In {sector}, every claim needs a citation from the provided sources — never answer uncited.",
            _ => null,
        };
        if (warning is null) return null;

        return new Lesson
        {
            Id = $"{Id}|{sector}|{trigger}",
            Agent = Id, Sector = sector, Trigger = trigger,
            Warning = warning, LearnedFrom = ctx.Ticker,
        };
    }

    private static Reward Fail(HashSet<string> fails, string critique) =>
        new(false, 0.0, new Dictionary<string, double> { ["specificity"] = 0.0 }, fails, critique);
}
