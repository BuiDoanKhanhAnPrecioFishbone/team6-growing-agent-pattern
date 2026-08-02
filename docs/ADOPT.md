# Adopt the Growing-Agent Pattern in your codebase

You write **one reward**. Everything else — the loop, the memory, trust, decay, the amplifiers — is off-the-shelf. This is the ~20-line integration for an agent you already have.

> Prefer to have it wired for you? Open this repo in Claude Code and run the **`apply-growing-agent`** skill — it finds your model call, writes a reward stub, and drops in the loop.

## 1. Reference the harness

```bash
dotnet add package AIAssistant.AgentHarness      # or reference the project / DLL
```

BCL-only. Inert offline (no `AGENT_LLM_*` set ⇒ no network, deterministic).

## 2. Describe your task as an `IAgent`

The only domain code you write. `Evaluate` — the reward — is the real work; use `Checks` so it's three lines.

```csharp
using AIAssistant.Harness;

sealed class MyAgent : IAgent
{
    public string Id => "my-agent";

    public Task<string> GenerateAsync(AgentContext ctx, IReadOnlyList<Lesson> lessons,
        string? critique, string? prior, int attempt, CancellationToken ct)
        => MyExistingModelCall(ctx, lessons, critique, prior, ct);   // wrap what you already have

    public Reward Evaluate(string draft, AgentContext ctx) => Checks.Of(
        ("cites a source",  draft.Contains("[")),
        ("under 200 words", draft.Split(' ').Length <= 200));        // failed labels become lessons

    public Lesson? LessonFor(string trigger, AgentContext ctx) => trigger switch
    {
        "cites a source" => new Lesson { Id = $"{Id}|*|cite", Agent = Id, Sector = "*",
            Trigger = trigger, Condition = "answering", Warning = "Always cite a source in [brackets]." },
        _ => null,
    };
}
```

## 3. Run it — everything below is off-the-shelf

```csharp
var harness = GrowingAgent.Quickstart("memory.json");   // JSON memory + env-driven options
var outcome = await harness.RunAsync(new MyAgent(), ctx, GrowingAgent.Options(), ct);
// outcome.BestDraft  → the answer
// outcome.LearnedLessons → what it just learned (grows, then stabilizes)
```

That's it. Run twice on the same task: the second run recalls the lesson and passes on the first try.

## Bring your own model
The harness talks to any OpenAI-compatible endpoint via `AGENT_LLM_*` (Azure AI Foundry, OpenAI, a local Llama/Phi). The **reward is the constant** — the loop doesn't care which model produced the draft. Swap the model; the pattern is unchanged.

```powershell
$env:AGENT_LLM_BASE_URL = "https://<resource>.openai.azure.com/openai/v1"
$env:AGENT_LLM_API_KEY  = "<key>"
$env:AGENT_LLM_MODEL    = "gpt-4.1-mini"     # or phi-4, llama-3, …
```

## Bring your own vector store
`ILessonStore` is the seam — four methods (`Retrieve` / `Write` / `RecordApplication` / `All`). Pick per deployment; the loop and agent never change:

| Backing | Use |
|---|---|
| `JsonLessonStore` | one file, zero infra (demo / default) |
| `InMemoryLessonStore` | RAM (tests) — the whole seam in ~40 lines |
| `SemanticLessonStore` | embeddings + LLM recall (once lessons accumulate) |
| `CosmosSemanticLessonStore` | Azure Cosmos, server-side vector search (cloud) |
| *your own* | pgvector / Qdrant / Azure AI Search — the same ~40-line adapter |

## Turn on amplifiers as you need them (off by default, non-breaking)
| Lever | Switch | What it buys |
|---|---|---|
| Best-of-N | `AGENT_SAMPLES=3` | biggest single lift for a cheap model |
| Self-verify | pass an `LlmCritic` to `Quickstart(..., critic:)` | catches soft errors the reward can't see |
| Escalation | `EscalateDraft` + `AGENT_LLM_MODEL_STRONG` | frontier quality only on the hard cases |
| Bounded context | `AGENT_LESSON_TOKENS=400` | a hard token ceiling on injected lessons |

## Graduate to weights (optional, later)
Every run exports training data (`TrainingExporter` / `RestEm`). When a lesson is stable and recurring, fine-tune it into the model (ReST-EM SFT from base) and `Graduation` prunes it from memory — same quality, zero context cost. See `slowloop`.
