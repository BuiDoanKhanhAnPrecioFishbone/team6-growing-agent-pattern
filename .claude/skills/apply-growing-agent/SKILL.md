---
name: apply-growing-agent
description: Wire the Growing-Agent Pattern into an EXISTING codebase. Use when a developer wants their LLM agent to learn from a reward and improve over time — turns a plain model call into a reward-driven fast loop with an episodic lesson memory. The dev writes one reward; everything else (loop, memory, trust, decay, amplifiers) is off-the-shelf.
---

# Apply the Growing-Agent Pattern

Goal: take an agent the developer ALREADY has (a prompt + a model call) and make it *grow* — reach higher quality and keep improving from a reward — by wrapping it in the harness. **The only thing the developer writes is the reward.** Do not rebuild their runtime; add a thin learning layer around their existing call.

## The mental model (say this to the dev)
> "You write a reward — a function that scores your own output. The harness does the rest: it retries on the reward's critique, samples the best of N, injects the lessons it has learned, and writes a new lesson when it fixes a mistake. Growing behavior is the free part."

## Steps

### 1. Find the seam
Locate the developer's existing LLM call (the function that takes a task and returns the model's text). That call becomes the body of `GenerateAsync`. Do not change their prompt or model — wrap them.

### 2. Add the harness
- If working inside this repo: reference `shared/AIAssistant.AgentHarness/AIAssistant.AgentHarness.csproj`.
- If a separate repo: `dotnet add package AIAssistant.AgentHarness` (or reference the built DLL). It is BCL-only and inert offline.

### 3. Implement `IAgent` — the ONLY domain code
Three methods. `Evaluate` (the reward) is the real work; the other two are usually trivial.

```csharp
using AIAssistant.Harness;

sealed class MyAgent : IAgent
{
    public string Id => "support-drafter";

    // (a) generate — wrap the dev's EXISTING call; inject any learned lessons into the system prompt.
    public async Task<string> GenerateAsync(AgentContext ctx, IReadOnlyList<Lesson> lessons,
        string? critique, string? prior, int attempt, CancellationToken ct)
    {
        var sys = "…the dev's existing system prompt…";
        if (lessons.Count > 0)
            sys += "\n\nRules learned from experience:" + string.Concat(lessons.Select(l => "\n• " + l.Warning));
        if (!string.IsNullOrEmpty(prior))                       // a revision round: fix, don't restart
            sys += $"\n\nYour previous attempt:\n{prior}\n\nProblems to fix:\n{critique}";
        return await TheirExistingLlmCall(sys, ctx.Input["task"]!.GetValue<string>(), ct);
    }

    // (b) evaluate — THE REWARD. Name each check; a failed label becomes the lesson it teaches.
    public Reward Evaluate(string draft, AgentContext ctx) => Checks.Of(
        ("answers the question", draft.Length > 0),
        ("cites a source",       draft.Contains("[")),
        ("under 200 words",      draft.Split(' ').Length <= 200));

    // (c) lesson — what to remember when a check failed (generic + reusable, not the one answer).
    public Lesson? LessonFor(string trigger, AgentContext ctx) => trigger switch
    {
        "cites a source" => new Lesson { Id = $"{Id}|*|cite", Agent = Id, Sector = "*",
            Trigger = trigger, Condition = "answering a question", Warning = "Always cite a source in [brackets]." },
        _ => null,
    };
}
```

Guidance for a good reward:
- Prefer **deterministic checks** (string/format/number/tool-execution) over an LLM judge — self-correction degrades without a grounded signal. Use an LLM critic only as an *extra* signal (pass one to the harness), never as the sole score.
- Make lessons **generic** ("cite a source"), never the specific answer — generic lessons transfer; memorized answers overfit.

### 4. Run it through the harness
Replace the dev's single call site with the loop. `Quickstart` wires a JSON memory + env-driven options.

```csharp
var harness = GrowingAgent.Quickstart("memory.json");   // JSON store; use QuickstartSemantic() for embeddings
var ctx = new AgentContext {
    Ticker = "case-123",
    Features = new AgentFeatures(sector: "support", tags: Array.Empty<string>(), situation: theTask),
    Input = new System.Text.Json.Nodes.JsonObject { ["task"] = theTask },
    AllowedSources = Array.Empty<string>(),
};
var outcome = await harness.RunAsync(new MyAgent(), ctx, GrowingAgent.Options(), ct);
// outcome.BestDraft is the answer; outcome.LearnedLessons is what it just learned.
```

### 5. Turn on amplifiers only as needed (all off by default, all non-breaking)
- Best-of-N: env `AGENT_SAMPLES=3` (biggest single lift for a cheap model).
- Self-verify: pass an `LlmCritic` to `Quickstart(..., critic:)`.
- Escalation: pass an `EscalateDraft` + set `AGENT_LLM_MODEL_STRONG` — pay for the big model only on hard cases.

### 6. Verify
Run twice on the same task. First run may revise/learn; the second should recall the lesson and pass on the first try (fewer iterations). Confirm `outcome.LearnedLessons` grows then stabilizes. Nothing should break offline — the harness is inert without `AGENT_LLM_*`.

## Choosing a store (bring your own)
`ILessonStore` is the seam — four methods. Pick per deployment; the loop and agent don't change:
- `JsonLessonStore` — one file, zero infra (default / demo).
- `SemanticLessonStore` — embeddings + recall (once lessons accumulate).
- `CosmosSemanticLessonStore` — Azure Cosmos server-side vector search (cloud).
- `InMemoryLessonStore` — RAM (tests). A pgvector / Qdrant / Azure AI Search backing is the same ~40-line shape.

## What NOT to do
- Don't replace their framework (MAF, LangChain, their own). This layer wraps whatever generates the draft — including a MAF/Foundry agent.
- Don't hand-write a reward that just calls the same model to grade itself as the sole score (self-enhancement bias). Ground it, or use a *different* model as judge.
- Don't inject the whole memory — retrieval already returns a bounded top-K; leave it bounded.
