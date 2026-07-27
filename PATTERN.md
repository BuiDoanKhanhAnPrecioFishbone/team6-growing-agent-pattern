# The Growing-Agent Pattern

> How we build every S-agent. Follow this and each agent gets **better run-to-run** on an
> Azure-Foundry-only, cost-optimized budget — no GPU required. S2 (Moat) is the reference
> implementation; `_template/` is your starting point.

---

## 1. The idea in three lines

1. An agent is a **policy** grown inside a deterministic **environment**, not a prompt you call once.
2. **One reward** gates the agent now (the loop) and could train it later (ART) — the same function.
3. Every run writes a **lesson** to a knowledge-graph memory; the next run retrieves it. Skill **compounds**.

Investing is too large to one-shot. So we give the agent tools, memory, and a reward, and let it grow.

---

## 2. Why this pattern is good

Each property is a deliberate engineering choice — and the reason judges and teammates trust the output.

- **Grounded by construction.** A hard gate rejects any fact not in the environment. The agent *cannot*
  hallucinate a number or invent a source — grounding is enforced, not hoped for.
- **The reward is deterministic.** Same draft → same score, forever. That makes it unhackable and
  reproducible, and safe to reuse as an RL reward later. No LLM-judge randomness inside it.
- **It compounds — cheaply.** The memory turns each run's mistake into a lesson the next run avoids.
  Fewer iterations over time = lower token cost. Quality ↑ while cost ↓, with no GPU.
- **One reward, two uses.** The exact object that gates the loop *is* the future training signal.
  Improving one improves the other — no wasted work, no divergence.
- **Uniform contract → parallelism.** Six agents, one shape (three methods). The team builds S1–S6 in
  parallel, each testable and composable, none blocking the others.
- **Human-in-the-loop is structural.** Judgment fields stay drafts until a person confirms at a gate;
  the reward fails any draft that pretends otherwise. Capital decisions never auto-fire.
- **Honest about signal.** We separate process quality (immediate) from did-the-thesis-play-out (laggy),
  learn from the first now, record the second for later — and never confuse them.
- **Future-proof for ART.** The lessons + high-score traces the memory accumulates *are* the training
  set. If GPU budget ever appears, ART slots in with zero rework.

---

## 3. Why a Foundry-hosted model can't run ART

ART (OpenPipe's Agent Reinforcement Trainer) makes a model better by **changing its weights** with
reinforcement learning (GRPO). Our contest gives us a **hosted inference endpoint** on Azure AI Foundry.
Those two things are fundamentally incompatible:

| What ART requires | What a hosted Foundry model gives you |
|-------------------|----------------------------------------|
| Open model **weights** you control | A **closed, inference-only** endpoint — weights never exposed |
| **Gradient computation + weight updates** (GRPO) | **No gradient/weight access** — you can only send prompts |
| A **GPU + vLLM** backend you drive during rollouts | A **managed API**, no GPU you own |
| Sustained **GPU hours** for the training run | Cheap **per-token** inference (the point of the scope) |
| — | **✓ This is the given constraint**: a Foundry LLM, called cheaply |

**Don't confuse these:** Azure *does* offer hosted fine-tuning (and a reinforcement-fine-tuning option on
some models). That is a provider-run, model-specific, paid service — **not** the same as running ART's
open-weight GRPO loop yourself, and not "just use a Foundry LLM cheaply." It is a heavier path we
deliberately do not depend on. To run ART itself on Azure you would deploy an *open-weight* model to GPU
managed compute — which breaks the cost-optimized, Foundry-hosted scope.

**So where does "growing" come from?** From the **fast loop + memory** — learning at *inference time*, by
changing the **context** we feed the model (retrieved lessons, critique-driven revision), **not** the
model's weights. That needs none of the four things ART requires. It runs on a plain Foundry endpoint, today.

---

## 4. Two speeds of learning

Our pattern is the **fast loop**. ART is the **slow loop** — an optional, GPU-bound upgrade. Both optimize
the exact same reward, so nothing is wasted. This is the single most important distinction for the team.

| | Fast loop — **OUR PATTERN** | Slow loop — ART (later, optional) |
|--|------------------------------|------------------------------------|
| What improves | the **context** (retrieved lessons + revision) | the base model's **weights** |
| When | every run, live | offline, in batches |
| Runs on | a plain **Foundry** inference endpoint | **GPU** compute + an open-weight model |
| Learns from | the reward + lessons, immediately | pooled high-score traces |
| Cost | per-token; **drops** as memory fills | GPU hours; one-off + redeploy |
| Extra needs | **none** | GPU · open weights · training backend |
| Same reward? | yes | yes — identical function |
| Status | **✓ built & proven now** | ⚙ architecture-ready, not required |

**The message:** we do **not** need ART to ship a growing agent. The fast loop already makes every S better
run-to-run inside the contest's constraints. ART is a slide that says "and here's how it scales further with
GPU" — not a dependency, not a risk on the demo.

---

## 5. Anatomy of the harness — parts & extension points

The shared library `shared/AIAssistant.AgentHarness` owns the machinery. Here is every part, what stays
fixed, and **what each agent extends**.

| Part | Role | What YOU extend (per agent) | Fixed (never edit) |
|------|------|------------------------------|---------------------|
| `IAgent` | the contract | the **three methods** | the interface |
| `AgentContext` | run input + environment handle | put your **environment** in `Input` (+ `AllowedSources`) | the shape |
| `AgentFeatures` | memory-scope keys | the `Sector` + any `Tags` you scope lessons by | — |
| `Reward` | reward shape | your `Breakdown` components + `FailedTriggers` keys | `Pass` / `Score` / `Critique` shape |
| `Lesson` | one episodic note | the `Warning` text, `Trigger` keys, scope | the `Id` scheme + hit-rate fields |
| `LessonStore` | the memory | the **backing store** (JSON → Cosmos) + richer retrieval | the three ops |
| `AgentHarness` | the fast loop | tuning via `HarnessOptions`; optional model **cascade** / **best-of-N** | the loop shape |
| `HarnessOptions` | tuning knobs | `MaxIters` / `Threshold` / `RetrieveTopK` (via env) | — |

### The extension points, concretely

- **Environment (`AgentContext`).** This is where each agent differs most. It is "what the agent cannot
  fabricate." S2 puts its *allowed source set* in `AllowedSources`; S3 will put its *computed calculator
  values* into `Input` and gate numbers against them (MemoLint-style). Same context type, different
  environment inside it.
- **Reward (`Evaluate`).** Fully agent-owned: your gates, your trigger keys, your 3–5 graded components.
  The only rule is it must stay deterministic (§9).
- **Generation (`GenerateAsync`).** Swap the mock for a live Foundry model via `ChatClient`. Two cheap
  upgrades the loop already supports in shape: a **model cascade** (small model first, escalate to a bigger
  one only when a gate fails) and **best-of-N** sampling per iteration (S3's original loop did this — pick
  the highest-scoring of N drafts before revising).
- **Retrieval (`LessonStore.Retrieve`).** Today it scopes by `agent + sector`, ranked by hit-rate. As a
  memory grows you can extend the match to `Tags` or to embedding similarity — the loop doesn't change.
- **Backing store.** The three ops (`Retrieve` / `Write` / `RecordApplication`) are the contract; the store
  behind them is swappable (see §6).

**The invariant:** the harness is agent-agnostic. If you're editing the loop or the store *for your agent*,
stop — that logic belongs in your `IAgent`.

---

## 6. Memory & the knowledge graph — how each agent stores what it learns

There are **two tiers** of memory. Keep them distinct.

**Tier 1 — the shared domain graph** (companies, statements/metrics, provenance, theses, evaluations).
One graph for the whole platform. Every agent appends its own block; the **candidate file is the
materialized view** of one company's subgraph at a point in time. This is *shared* across all agents — S2's
moat block, S3's financials block, S4's valuation block all hang off the same `Thesis`.

**Tier 2 — the per-agent lesson memory** (the episodic "how to do *my* job better" notes). This is what
`LessonStore` holds, and it is **partitioned by agent**. Each agent only ever reads and writes its own
lessons; it never sees another agent's.

```
(Company)──has──(Statement/Metric)──sourced_from──(Provenance)      ┐
   └──has──(Thesis)──evaluated_by──(Evaluation)                     │  Tier 1: SHARED domain graph
                          └──produced──(Lesson)                     ┘  (the candidate file materializes it)

Lesson.Id = "{agent}|{sector}|{trigger}"                            ┐  Tier 2: PER-AGENT lesson memory
   e.g. "s2-moat|consumer_staples|UNCITED_SOURCE"                   │  partition = agent, scope = sector,
   fields: warning, learnedFrom, timesApplied, timesHelped, hitRate ┘  key = the mistake it prevents
```

### Partition & retrieval

- **Partition key = the agent** (`s2-moat`, `s3-financials`, …). Each agent's memory is its own logical bucket.
- **Scope = `sector`** (+ optional `tags`). Retrieval is `WHERE agent = me AND sector = this company's
  sector ORDER BY hitRate DESC LIMIT topK` — bounded, so the prompt is never flooded.
- **Key = `trigger`** (the mistake type). This makes lessons upsert (re-learning refreshes text, keeps stats)
  and lets each lesson track its own hit-rate so the memory self-corrects.

### Storage progression (same three ops — only the backing changes)

| Stage | Backing | Why / when |
|-------|---------|------------|
| **Now (demo)** | one **JSON file per agent** (`lessons.json` in the agent's output dir; path via `S2_LESSON_STORE` / `AGENT_LESSON_STORE`) | offline, inspectable via `GET /lessons`, zero infra |
| Single-node | **SQLite** table `lessons`, PK `(agent, sector, trigger)` | one process, still file-simple, real queries |
| **Azure-native (recommended)** | **Cosmos DB** container, **partition key `/agent`** | each agent's memory is its own partition; scoped reads stay cheap; co-locates with Tier-1 if you use the Gremlin (graph) API |
| Full graph | Cosmos **Gremlin** / Neo4j — `Lesson` nodes with `[:LEARNED_FROM]->Thesis`, `[:APPLIED_IN]->Run` edges | when you want lesson↔thesis↔company traversal and provenance of *why* a lesson exists |

Because `LessonStore`'s three methods are the only contract, moving from JSON → Cosmos is a store swap, not
a rewrite: no agent code and no loop code changes.

**How an agent's slice relates to the whole:** an agent's lessons are one labeled partition of the platform
graph. The *domain* nodes it produces (its evaluations, its output block) feed the **shared** Tier-1 graph;
its *lessons* stay in its **own** Tier-2 partition. That separation is why six agents can learn in parallel
without stepping on each other, while still contributing to one company knowledge graph.

---

## 7. What you build (the contract — three methods)

```csharp
public interface IAgent
{
    string Id { get; }                       // "sN-name" — stable; it is your memory partition key
    Task<string> GenerateAsync(AgentContext ctx, IReadOnlyList<Lesson> lessons,
                               string? critique, string? priorDraft, int attempt, CancellationToken ct);
    Reward Evaluate(string draft, AgentContext ctx);          // THE REWARD — the crux
    Lesson? LessonFor(string trigger, AgentContext ctx);      // a fixed mistake → a reusable lesson
}
```

That is the whole job. `_template/TemplateAgent.cs` is a runnable skeleton of these three.

---

## 8. The five decisions every agent author makes

Before writing code, answer these. They *are* the design of your agent.

1. **Environment (source of truth).** What can the agent NOT fabricate? Put it in `AgentContext`.
2. **Hard gates.** The pass/fail rules that zero the score; name each with a stable trigger key.
3. **Graded components.** 3–5 quality dimensions in `[0,1]` with weights summing to 1. Deterministic only.
4. **Features (memory scope).** What makes a lesson relevant later? At minimum `Sector`.
5. **Lessons.** For each fixable trigger, the scoped, conditional guidance to inject next time.

---

## 9. The invariants (non-negotiable)

- **Grounding by construction.** The agent never invents environment facts; the hard gate enforces it.
- **The reward is deterministic.** No randomness, no LLM-as-judge inside `Evaluate`.
- **One reward, two uses.** The object that gates the loop is the training signal. Never fork them.
- **Drafts stay drafts.** Human-judgment fields carry `humanConfirmed:false` until a gate.
- **Lessons are conditional & hit-rated.** Scope every lesson; let the store decay the ones that stop helping.
- **Two signals kept apart.** Process quality (now) ≠ thesis outcome (later).
- **Cost lives in the harness.** Retrieval budget, stop-at-target, capped iterations, environment-does-the-math.

---

## 10. Build checklist (copy → fill → run)

```
[ ] 1. cp -r agents/_template agents/sN
[ ] 2. Rename the .csproj, RootNamespace, Id ("sN-name"), and the port in launchSettings.json
[ ] 3. Define your block shape (see any agent's *Agent.cs, e.g. s2-moat/MoatAgent.cs)
[ ] 4. Decide the FIVE decisions (§8) — write them at the top of your agent as a comment
[ ] 5. Implement Evaluate  (the reward: gates → triggers → graded → critique)   ← spend your time here
[ ] 6. Implement GenerateAsync  (Foundry model via ChatClient; keep a deterministic mock for offline demo)
[ ] 7. Implement LessonFor  (one scoped warning per fixable trigger)
[ ] 8. dotnet build && dotnet run   →   POST /run twice, GET /lessons
```

Your output block appends to the shared **candidate file** under your key (`screen`, `moat`,
`financials`, `valuation`, `allocation`, `monitoring`). One agent owns one key. No orphan output.

---

## 11. Verification (the bar an agent must clear)

1. **Gates bite.** A draft that violates a hard rule scores 0 and never outranks a passing draft.
2. **It grounds.** Every environment fact in the output traces to a source/computed value.
3. **It compounds.** Two same-sector companies on a fresh store: run 1 makes and fixes a mistake
   (iterations > 1, writes a lesson); run 2 has the lesson injected and gets it right first try
   (fewer iterations, high `firstScore`); the lesson's `hitRate` climbs.
4. **It degrades safely.** With no Foundry endpoint set, `/run` still returns (mock/last-known), never 500s.

---

## 12. The reference: what "compounding" looks like (S2)

Fresh memory, offline mock, two consumer-staples companies:

```
RUN 1 · VNM   iterations: 2   firstScore: 0 (invented a citation → gate)   best: 1.0   LEARNED a lesson
RUN 2 · MSN   iterations: 1   firstScore: 1.0 (lesson injected up front)   best: 1.0   lesson hitRate → 1.0
```

Run 1 stumbles and fixes it; run 2 never stumbles. Fewer iterations = **lower token cost**.

**Reference implementations:** `s1-screen` … `s6-monitor` · **Full flow:** `orchestrator` · **Start here:** `_template`.

---

## 13. Later: the slow loop (ART) — an upgrade, not a dependency

The lessons + high-scoring traces the memory accumulates ARE the training set. When GPU budget exists,
ART/GRPO fine-tunes the base policy against the **same `Reward`**, then redeploys OpenAI-compatible to
Foundry — only the `*_LLM_BASE_URL` env var changes. Until then, the fast loop already makes the agent
better every run. Do not block on training.
