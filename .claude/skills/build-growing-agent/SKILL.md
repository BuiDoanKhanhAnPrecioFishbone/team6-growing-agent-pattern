---
name: build-growing-agent
description: Use when adding or modifying an agent in the Growing-Agent harness — i.e. implementing `IAgent` on `AIAssistant.AgentHarness` so it learns run-to-run (a reward, a lesson memory, a human gate). Trigger on requests like "add an agent", "create sN", "build the valuation/screen/... agent", "implement the reward for X", or "wire a new IAgent" in this repo. Produces a new self-contained agent that plugs into the shared harness without touching it.
---

# Build a Growing-Agent

You are scaffolding a **new agent** on this repo's shared harness. An agent is a *policy* in a deterministic
*environment*, driven by **one reward** that gates it now (and trains it later), writing a **lesson** to a
self-refining memory after every run so the next run is smarter.

**Golden rule: never edit the harness.** The loop, memory, reward-shape, embeddings, recall, tool-use, and
human-gate machinery live in `shared/AIAssistant.AgentHarness` (+ `.Cosmos`, `.AgentHost`). You implement
**three methods** and nothing else. If you feel the urge to change the loop or the store *for your agent*,
stop — that logic belongs in your `IAgent`.

`PATTERN.md` at the repo root is the **source of truth**. If this skill and `PATTERN.md` ever disagree,
`PATTERN.md` wins — then update this skill (see "Keeping in sync").

## 1. Scaffold (copy, don't write from scratch)
```
cp -r _template  sN-<name>          # e.g. s4-valuation
```
Then in the new folder:
- rename the `.csproj` and its `<RootNamespace>`,
- set the agent's `Id` to `"sN-<name>"` (this is its memory partition key — keep it stable),
- set the port and `blockKey` in `Program.cs` (`Host.Run(args, new YourAgent(), <port>, "<candidateFileKey>")`).

The reference implementations to copy patterns from: **`s2-moat`** (qualitative, cite-or-drop reward) and
**`codeagent`** (real reward = unit tests). Read one before you start.

## 2. Implement the three methods (`IAgent`)
```csharp
string Id { get; }                                   // "sN-name" — stable
Task<string> GenerateAsync(ctx, lessons, critique, priorDraft, attempt, ct);  // produce a draft
Reward Evaluate(string draft, AgentContext ctx);     // THE REWARD — the crux
Lesson? LessonFor(string trigger, AgentContext ctx); // a fixed mistake → a reusable, scoped lesson
```
- **GenerateAsync** — build the draft. Inject `lessons` into your prompt so past mistakes are avoided up
  front; on a revision, `critique` + `priorDraft` come back — *fix, don't restart*. Call the model via
  `AgentHost.Model.Generate(...)` (Foundry when `AGENT_LLM_*` is set) or return a deterministic mock so it
  runs offline. Keep a mock path — it makes the demo/tests work with no endpoint.
- **Evaluate** — the reward. Deterministic only (no randomness, no LLM-as-judge inside it). Structure:
  `hard gates → FailedTriggers → graded components (weights sum to 1) → Critique`. A gate failure returns
  score 0 so a bad draft never outranks a good one.
- **LessonFor** — map each *fixable* trigger to a short, **scoped, conditional** lesson
  (`Id = "{Id}|{sector}|{trigger}"`). Return `null` for triggers that make no transferable rule (e.g. SCHEMA).

## 3. Answer the FIVE decisions before coding (put them as a comment at the top of your agent)
1. **Environment** — what can the agent NOT fabricate? Put it in `AgentContext` (`AllowedSources`, or
   computed values in `Input`). This is where agents differ most.
2. **Hard gates** — the pass/fail rules that zero the score; name each with a stable trigger key.
3. **Graded components** — 3–5 quality dimensions in `[0,1]`, weights summing to 1.
4. **Features** — what makes a lesson relevant later? At minimum `Sector`; set `Features.Situation` to a
   short text of the case so semantic retrieval works.
5. **Lessons** — for each fixable trigger, the conditional guidance to inject next time.

## 4. Invariants (non-negotiable)
- **Grounding by construction** — the agent never invents environment facts; the hard gate enforces it.
- **Deterministic reward** — same draft → same score, forever (unhackable, and reusable as an RL reward).
- **One reward, two uses** — the object that gates the loop is the training signal. Never fork them.
- **Drafts stay drafts** — human-judgment fields carry `humanConfirmed:false` until a gate.
- **Lessons are conditional & hit-rated** — scope every lesson; the store dedupes, quarantines injections,
  and promotes on hit-rate or a human gate. Don't re-implement that.
- **One agent owns one candidate-file key** (`screen`, `moat`, …). No orphan output.

## 5. Verify (the bar — an agent isn't done until all pass)
1. `dotnet build GrowingAgentPattern.slnx` is green (add your project to the `.slnx`).
2. **Gates bite** — a draft that breaks a hard rule scores 0.
3. **It grounds** — every environment fact traces to a source/computed value.
4. **It compounds** — run two same-sector cases on a fresh store: run 1 makes & fixes a mistake (writes a
   lesson), run 2 has it injected and gets it right first try (fewer iterations, higher `firstScore`).
5. **It degrades safely** — with no `AGENT_LLM_*`, `/run` still returns (mock), never 500s.

## 6. Wire it in
- Add the project to `GrowingAgentPattern.slnx`.
- If it's part of the pipeline, add `("<key>", new YourAgent(), "<gate label or null>")` to the orchestrator
  and the UI pipeline arrays.
- `dotnet run --project sN-<name>` → `POST /run` twice → `GET /lessons` and confirm the lesson appears.

## Keeping in sync (why this skill exists)
This skill is the distributable form of the pattern. When the **shared harness changes** (new `IAgent`
member, new memory capability, new invariant), update it in one place:
1. update `shared/AIAssistant.AgentHarness` + `PATTERN.md`,
2. edit **this** `SKILL.md` to match,
3. commit. Teammates re-pull / re-sync their skills folder, and every coding agent now scaffolds to the new
pattern automatically. Treat `PATTERN.md` as truth and this skill as its executable checklist.
