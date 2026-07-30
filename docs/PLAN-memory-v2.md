# Plan — Memory v2: verified, self-refining, tool-accessible

> Two-week build. Upgrades the flat exact-match lesson store into a memory that retrieves the
> **applicable** lessons (not just similar), **self-refines** (dedup / generalize / conflict / prune),
> is **safe against poisoned lessons**, and is **proven** by a measured A/B — plus a tool seam so agents
> can *call* their memory (and MCP tools). Bakes in the patterns we adopted from Claude Code.

## 0. Two guardrails (hold these or the project drifts)
1. **Vector-first, graph only where you traverse.** Start with embeddings + metadata + LLM recall. Add
   graph edges (`supersedes`, `conflicts_with`) *only* when a query needs traversal. No graph for its own sake.
2. **The memory plumbs the learning signal — it isn't the signal.** Pretty retrieval over bad lessons =
   fast wrong answers. Keep a **real reward** feeding it. → build the A/B on a **real-reward task** (a code
   agent whose reward = tests pass), not the planted flaws. Memory quality is downstream of reward quality.

---

## 1. What changes (and what doesn't)
The harness, `IAgent`, and agents **do not change**. The whole upgrade lives behind the existing
`ILessonStore` seam as a new backing:

```
JsonLessonStore (today)  ─┐
                          ├─ ILessonStore   ← harness/agents call this, unchanged
SemanticLessonStore (v2) ─┘   (retrieval + write are smarter inside)
```

One small ripple: retrieval needs to know the **current situation** to recall against. Extend the scoping
keys the harness already passes:

```csharp
// today:  record AgentFeatures(string Sector, IReadOnlyList<string> Tags);
record AgentFeatures(string Sector, IReadOnlyList<string> Tags, string Situation = "");
// the host/orchestrator fills Situation with a short text of the case (ticker + industry + key facts).
```

Backing store stays swappable (JSON for dev, Cosmos for cloud) — v2 just adds a vector index + an
embedding/recall model call.

---

## 2. Lesson v2 data model
```csharp
class Lesson {
  string Id, Agent, Sector;
  LessonType Type;          // NEW: GroundingRule | ToolTip | DomainFact | Strategy
  string Condition;         // NEW: when it applies — "sector=banking AND d/e>1"  (short, embeddable)
  string Warning;           // the guidance (the heavy text — loaded phase-2 only)
  float[] Embedding;        // NEW: vector of (Condition + one-line summary)
  Trust Trust;              // NEW: Provisional | Verified | Quarantined
  string LearnedFrom;       // provenance: run/case id (audit + training export)
  string Date, LastUsed;    // NEW LastUsed → staleness
  int TimesApplied, TimesHelped; double HitRate;
  // graph edges — add later, only if traversed:
  // string[] Supersedes, ConflictsWith;
}
```
`Type` and `Trust` are the two additions that pull the most weight (typing → better recall & pruning;
Trust → injection defense).

---

## 3. Retrieval — hybrid LLM-recall + two-phase (the Claude Code steal)
Claude Code found an **LLM side-query beats embedding search** for memory recall — because relevance here
is *applicability*, not *similarity*. We hybridize: embeddings shortlist cheaply, an LLM picks what applies,
and only then do we load full text (two-phase).

```
RetrieveAsync(agent, features, topK):
  1. FILTER (metadata, cheap):  agent == me
                                AND (sector == features.Sector OR sector == "*")
                                AND Trust == Verified
                                AND not stale-expired
  2. SHORTLIST (vector):  embed(features.Situation) → cosine top-N (N≈12) over candidate embeddings
  3. RECALL (LLM side-query, cheap model = AGENT_LLM_*):
        "Case: {Situation}. Candidates: [{id, condition, one-line}]. Return the ids that ACTUALLY apply."
        → applicable ids (≤ topK)
  4. LOAD phase-2:  fetch full Warning text ONLY for the selected ids   ← two-phase loading
  5. return those lessons
```
Notes
- Steps 1–2 keep the LLM call small (only ~12 short candidates go to the recall model). Cheap.
- **Fallback:** if the recall model is disabled/unreachable, degrade to step-2 vector top-k (never break).
- Embeddings: Azure OpenAI `text-embedding-3-small` deployment (`AGENT_EMBED_*` env) — Foundry-native, no GPU.

---

## 4. Write path — refine + injection defense
A *learned* lesson is untrusted input that will be injected into a prompt → it is a **prompt-injection
surface**. This is the pattern to steal from Ch 12 (snapshot/validate), and it matters more for us than for
Claude Code because our context is learned, not human-authored.

```
WriteAsync(lesson):
  1. VALIDATE (injection defense):
       - reject/kill instruction-injection patterns ("ignore previous", "system:", exfil, tool calls)
       - it must be advice ABOUT THE DOMAIN, not about the agent's meta-behavior
       - length/'shape bounds
       - on suspicion → Trust = Quarantined (stored, never injected, flagged for human)
       - else → Trust = Provisional
  2. EMBED (condition + summary)
  3. DEDUP:      cosine to an existing lesson (same agent) > θ → MERGE (keep stats, refresh text), don't add
  4. CONFLICT:   semantically opposite to a Verified lesson → mark conflict, don't auto-apply both; gate it
  5. persist
Promotion to Verified: a Provisional lesson becomes Verified when it (a) earns hit-rate over K applications,
  OR (b) a human confirms it at a gate. ONLY Verified lessons auto-inject (step 1 of retrieval).
Background jobs (not per-write):
  GENERALIZE: cluster specifics; LLM proposes a general rule → add general, demote covered specifics.
  PRUNE:      hitRate < floor AND stale (LastUsed old) → archive (never hard-delete; keep for audit/training).
```
**Injection rule of thumb:** lessons are injected as *clearly-delimited advisory observations*, never as
system instructions. Verified-only auto-inject. Provenance on every lesson.

---

## 5. Tool layer + MCP seam (adopt Claude Code Ch 6–7, 15)
Give the agent tools — starting with the ability to *call its own memory*. This turns single-shot block
generation into a real tool-use loop, and it's where "USE knowledge better" becomes concrete.

```csharp
interface ITool { string Name, Description; JsonNode Schema; bool ReadOnly; Task<string> InvokeAsync(JsonNode args, CancellationToken ct); }
```
- **Safety partition (Ch 7):** read-only tools run freely; mutating/outward tools require a gate. This keeps
  grounding intact — **tools widen what the agent KNOWS; the reward still governs what it OUTPUTS.**
- **Built-in tools (in-process, week 2):** `memory.search(query)` (retrieval as a tool), `compute(...)`
  (deterministic calculators), `fetch(...)` (data). All read-only → no gate.
- **MCP seam (Ch 15):** `McpToolSource` connects an MCP server and wraps its tools as `ITool`. MCP tools
  default **gated** (untrusted) until the operator marks them read-only. For two weeks: ship the interface +
  one demo MCP tool; full transport/OAuth is a fast-follow.
- **The beyond-Claude-Code move:** log **tool-use lessons** ("call `compute` before narrating", "this tool's
  result needs verifying"). The agent learns to use its tools better — memory + tools fused.

---

## 6. The proof — the A/B eval (this is the deliverable that matters)
Without this it's infrastructure; with it, it's a proven claim.

- **Task:** a **code agent** — "write a function that passes these unit tests." Reward = run the tests
  (deterministic, unhackable). This gives *real* learning (the model genuinely fails and improves), and it
  proves the harness is **domain-agnostic** (not just finance).
- **A/B:** same agent, same case sequence, two memories — **exact-match (v1)** vs **semantic+refined (v2)**.
- **Measure over the sequence:** pass-rate, avg score, **iterations-to-pass**, and **retrieval precision**
  (are injected lessons actually applicable?).
- **Output:** a **learning curve** — v2 higher and steeper than v1. That chart is the pitch. Also report the
  LLM-recall vs pure-embedding retrieval precision (validates the Claude Code steal on your data).

---

## 7. Two-week schedule (and explicit cuts)
**Week 1 — memory**
- D1–2: `Lesson v2` + `SemanticLessonStore : ILessonStore` skeleton (metadata filter + embedding shortlist,
  Azure embeddings); add `AgentFeatures.Situation`; keep JSON/Cosmos backing.
- D3–4: LLM-recall side-query + two-phase loading, wired into `RetrieveAsync` (+ graceful fallback).
- D5: `WriteAsync` refine v1 — validate (injection) + embed + dedup + Trust tiers.

**Week 2 — proof + tools**
- D6–7: A/B eval harness + the code-agent task + the learning-curve chart. **← the money artifact.**
- D8–9: in-process tool loop + `memory.search` + `compute` tools (read-only); reward still gates output.
- D10: MCP seam (interface + one demo tool) + write up results; update the positioning artifact's top-right
  dot with the *measured* curve.

**Cut first if time slips (in order):** MCP → interface-only. Generalize/prune → manual/background stub.
Graph edges → skip entirely (vector + metadata is enough until a traversal query appears). Tool-use-lesson
logging → nice-to-have.

---

## 8. Interfaces to add (sketch)
```csharp
// embeddings + recall, Foundry-native (reuse Model.cs patterns)
interface IEmbedder { Task<float[]> EmbedAsync(string text, CancellationToken ct); }   // Azure text-embedding-3-small
static class Recall { Task<IReadOnlyList<string>> ApplicableAsync(string situation, IReadOnlyList<(string id,string cond,string oneLine)> candidates, int k, CancellationToken ct); }

// the v2 store — same ILessonStore seam, smarter inside
sealed class SemanticLessonStore : ILessonStore {
  // RetrieveAsync: filter → embed(situation) → shortlist → Recall.ApplicableAsync → load phase-2
  // WriteAsync:   validate → embed → dedup/merge → conflict-check → persist(Provisional)
  // + PromoteAsync(id) on human gate / hit-rate; background GeneralizeAsync/PruneAsync
}

// tools
interface ITool { /* Name, Description, Schema, ReadOnly, InvokeAsync */ }
sealed class MemorySearchTool : ITool { /* read-only; wraps SemanticLessonStore */ }
sealed class McpToolSource { /* connect → wrap MCP tools as ITool (gated by default) */ }
```

## 9. Risks / open questions
- **Recall model cost/latency** per retrieval (one small call). Mitigate: cache by situation-hash; only
  candidates' one-liners go to the model.
- **Generalization is the hardest leg** — LLM-proposed general rules can be wrong. Keep them Provisional and
  human-gated before Verified.
- **Situation text quality** drives recall quality — define it well per agent (features + a couple of facts).
- **When does graph earn its place?** The moment you want "lessons about entities related to this one" or
  "which lessons this one supersedes." Until then: don't build it.

---
**Composes with:** the code-agent keystone (real reward → real lessons), the tools/MCP extensibility
(Claude Code Ch 6–7,15), and the positioning artifact (§4 refine leg, §5 recalled→verified memory).
