# The Growing-Agent Pattern — Story, Design & Q&A

A single reference for presenting and defending the work. Part 1 is the story you tell. Parts 2–5 are how it works, how each piece is built, and why it's efficient, with references. Part 6 is the prepared Q&A by audience. Part 7 is the honesty ledger — what is measured, what is a mechanism demo, what is designed-but-not-run — so nobody gets caught overclaiming.

---

## Part 1 — The story (the version you say out loud)

**The problem.** Frontier models are expensive. The cheap models (gpt-4.1-mini, Phi, Llama) are 5–20× cheaper but make more mistakes and don't get better — every conversation starts from zero. Teams reach for fine-tuning (needs a GPU, labeled data, and MLOps) or just pay for the big model.

**The insight.** You don't need a bigger model or a GPU to make an agent better. You need a **reward** and a **loop**. Wrap a cheap model in a harness that: scores its own output against a reward, retries on the critique, keeps the best of a few samples, and — the key part — **writes down what it learned as a "lesson"** and recalls it next time. The model's weights never change; its *behavior* compounds because its *memory* compounds.

**What we built.** A portable harness (BCL-only, runs on Azure AI Foundry, no GPU) with:
- a **fast loop** (generate → evaluate → retrieve lesson → revise → best-of-N → escalate → learn),
- a **governed memory** (trust states, decay, eviction, conflict-detection, consolidation, injection defense),
- a **scoped memory** (global ↔ team ↔ user) so the same loop **personalizes per user** — no per-user fine-tuning,
- a **skill tier** (reusable *procedures*, not just rules),
- and a **flywheel** that exports every run as training data, so the same reward can later fine-tune the model (the "slow loop").

**The economics — the diamond.** The most expensive thing in AI is labeled data. We get it *free*: every time someone uses the agent, the reward labels which step was right or wrong. Ordinary usage is the coal; the reward loop is the press; verified lessons + labeled examples are the diamonds. No user research, no labeling budget — ~2.7 labeled examples mined per interaction, forever.

**The proof.** On a live Foundry model, gpt-4.1-mini + harness matches gpt-5.1's pass rate, and escalation reaches frontier quality at ~72% of the cost. Everything else — compounding, poisoning-resistance, skill-transfer, self-summarizing memory, personalization, and the free-data meter — is proven by deterministic benchmarks you can re-run in one command.

**The honest line (say this — it builds trust).** "We're not a competitor to Microsoft Agent Framework or Foundry — those are the runtime. We're the *learning layer* they don't have. And where we overlap with them (tool-calling, MCP), use theirs; ours is deliberately minimal so it's portable."

---

## Part 2 — How we think about the pattern (the mental model)

**Two loops.**
- **Fast loop (in-context, no GPU):** the agent improves *now* by revising within a run and recalling lessons across runs. This is what ships.
- **Slow loop (weights, later):** when a lesson is stable and recurring, fine-tune it into the model and prune it from memory. The fast loop *produces the training data* for the slow loop.

**The reward is the object.** One `Reward` value does triple duty: it *gates* the answer now, it *writes* the lesson, and it *labels* the training data later. The whole field converged on "sample → score → keep/weight by reward" — we make that score the pivot of both loops.

**Memory is a governed system, not a log.** A memory that only appends will rot. Ours curates itself: decays stale lessons, evicts the least valuable, demotes contradictions, merges duplicates, folds related lessons into meta-rules, and refuses poisoned ones. Every lesson has a *trust state* it must earn.

**One sentence:** *A cheap model, wrapped in a reward loop with a governed memory, reaches frontier quality and keeps improving from ordinary use — no GPU, portable onto any runtime.*

---

## Part 3 — The parts (what · how · implement · code · why efficient · references)

### 3.1 The fast loop — `AgentHarness`
- **What:** the reusable substrate every agent plugs into. Agent-agnostic.
- **How it works:** retrieve top-K lessons → for each iteration draw N samples, evaluate each with the reward, keep the best → if below the bar, revise on the critique → optionally escalate to a stronger model → record which lessons helped → write a lesson for every mistake it fixed.
- **Implement:** implement `IAgent` (3 methods), call `harness.RunAsync(agent, ctx, opts, ct)`.
- **Code:** `shared/AIAssistant.AgentHarness/Harness.cs`.
- **Why efficient:** improvement is O(top-K) in context and needs no training; a cheap model with a wide quality spread benefits enormously from best-of-N + revise.
- **References:** Reflexion (verbal RL), Self-Refine (iterative critique), CoALA (agent memory taxonomy).

### 3.2 The reward — `Reward` + `Checks`
- **What:** a function that scores your own output. The only thing an adopter writes.
- **How:** `Checks.Of(("names a source", hasCite), ("under 200 words", short))` → Pass, a graded Score, the failed labels (which become learning triggers), and a critique the revise step reads.
- **Code:** `Harness.cs` (`Reward`), `Ergonomics.cs` (`Checks`).
- **Why efficient:** deterministic checks are free, reproducible, and un-hackable; a failed check *is* the lesson, so one list drives scoring and learning.
- **References:** CRITIC and "LLMs Cannot Self-Correct Yet" (self-correction degrades without a grounded signal); GRPO/RULER (relative scores suffice — don't over-calibrate).

### 3.3 The memory — `ILessonStore` + `SemanticLessonStore`
- **What:** the agent's episodic memory of lessons, behind one swappable interface.
- **How:** metadata filter → embed the situation → cosine shortlist → cheap LLM "which apply?" rerank → two-phase load. Governance: recency **decay**, capacity **eviction**, **conflict**-demotion, **dedup**-merge, hierarchical **consolidation**, **injection** defense, **importance** weighting, **bi-temporal** supersede, **corroboration-gated** promotion.
- **Code:** `SemanticLessonStore.cs`, `Recall.cs`, `Conflict.cs`, `Consolidation.cs`, `Importance.cs`, `MemoryAudit.cs`.
- **Why efficient:** retrieval returns a bounded top-K, so context cost is independent of memory size; consolidation keeps it small at scale; a two-phase load sends only (id, summary) to the reranker, not full text.
- **References:** Generative Agents (recency+importance+relevance, reflection), Mem0 (ADD/UPDATE/DELETE), Zep/Graphiti (bi-temporal), A-MEM (linked notes), MemGPT (paging/eviction).

### 3.4 The amplifiers — best-of-N, self-verify, web-search, escalation
- **What:** off-by-default levers that raise a cheap model's quality at inference time.
- **How:** `AGENT_SAMPLES=N` (best-of-N); an `ICritic` (self-verify); a keyless `WebSearchTool` (grounding); an `EscalateDraft` + `AGENT_LLM_MODEL_STRONG` (cheap-first, escalate on hard cases).
- **Code:** `Harness.cs`, `Tools.cs`.
- **Why efficient:** compute-optimal — spend more only where it helps; escalation pays for the big model only on the hard minority.
- **References:** Self-Consistency, Scaling Test-Time Compute (Snell), Tree-of-Thoughts (cost caveat), CRITIC (tool-grounded verify).

### 3.5 The skill/procedure tier — `SkillExtractor`
- **What:** the tier above a one-line rule — a reusable *method* (ordered steps) that transfers to new tasks.
- **How:** contrastive extraction (the procedure a *passing* answer used that a *failing* one missed) → verify-gate (commit only if following it reproduces a pass) → stored as `LessonType.Procedure` so it inherits all memory governance.
- **Code:** `SkillExtractor.cs`; proof in `skillbench`.
- **Why efficient:** a procedure carries a multi-step method where a rule can't; AWM reports +24–51% from exactly this.
- **References:** ExpeL (contrastive insights), Agent Workflow Memory (procedures), Voyager (verified skill library).

### 3.6 The slow loop — `TrainingExporter` + `RestEm` + `Graduation`
- **What:** turn accumulated experience into weights, then prune it from context.
- **How:** every run exports SFT/preference/RL JSONL (verified-gated); `RestEm.Select` rejection-samples the passing trajectories into a chat SFT set; fine-tune from base + general-data mix; `Graduation` re-tests each lesson on the baked model without injection and evicts what the weights absorbed.
- **Code:** `Training.cs`, `SlowLoop.cs`; arc in `slowloop`.
- **Why efficient:** SFT-first is the cheapest, most stable slow loop (no RL infra); verified gating is the documented antidote to model collapse; graduation drops the per-call token cost to zero for baked knowledge.
- **References:** STaR / ReST-EM (rejection-sampling SFT from base), FireAct/AgentTuning (trajectory SFT + data mixing), GRPO/RULER & ART (the RL rung), the "verified data or collapse" literature.

### 3.7 Context management — `Context`
- **What:** keep a long session (and its cost) bounded.
- **How:** compact old turns to gist while keeping recent detail sharp; a token-budget cap on injected lessons (`FitLessons`); structure-preserving tool-history trimming.
- **Code:** `Context.cs`.
- **Why efficient:** cost and latency stay flat no matter how long the session runs.

### 3.8 Security — guardrails for learning
- **What:** defend the *memory write path*, the attack surface Foundry's I/O guardrails don't see.
- **How:** injection validation → quarantine; corroboration-gated promotion (a lesson must help across ≥2 distinct situations, so a poison can't self-promote by repeating one case); a periodic `MemoryAudit` sweep.
- **Code:** `SemanticLessonStore.cs`, `MemoryAudit.cs`; proof in `guardbench` (15/15).
- **References:** OWASP Agentic Top-10 (persistent memory poisoning), MINJA-style memory-injection attacks (>95% vs input-only guards), MemAudit.

### 3.10 The data thesis — `DataValue` (coal → diamonds)
- **What:** put a number on the training data mined for free from ordinary use.
- **How:** every run exports verified lessons + labeled examples (SFT/preference/RL lines); `DataValue.Estimate` counts them and, at a configurable per-example rate, the labeling spend avoided.
- **Code:** `DataValue.cs`; the meter + each lesson's before→after in `diamonds`.
- **Why efficient:** the single biggest cost in improving an AI — labeled data — becomes a byproduct of usage, not a budget line. The reward labels every step; you never run a labeling project.
- **References:** the implicit-feedback / RLHF lineage — but step-level and in-context, not a final thumb feeding a quarterly retrain.

### 3.11 Personalization — `Scope` (smarter *and* yours)
- **What:** the same free-data loop, scoped, gives per-user personalization.
- **How:** a lesson has an `Owner` — global / `tenant:x` / `user:y`. Retrieval merges the scopes that apply and ranks the most specific first (user > tenant > global); the harness stamps learned lessons with the session's scope and namespaces the id so users never collide.
- **Code:** `Scope.cs`, `AgentFeatures.User/Tenant`, `Lesson.Owner`; proof in `personalize`.
- **Why efficient:** personalization with **no per-user fine-tuning** — it's a scope, not a new system; a brand-new user inherits global/team lessons on day one (no cold-start void), then personalizes from their own use.
- **References:** hierarchical/multi-tenant memory; this credibility-axis scoping is our own.

### 3.9 Adoption — the skill, the package, the seam
- **What:** make it trivially adoptable — you write one reward.
- **How:** the `apply-growing-agent` skill wires it into an existing repo; `GrowingAgent.Quickstart` is a one-call setup; `ILessonStore` is a ~40-line seam for any store.
- **Code:** `.claude/skills/apply-growing-agent/SKILL.md`, `Ergonomics.cs`, `InMemoryLessonStore.cs`, `docs/ADOPT.md`.

---

## Part 4 — Why it's efficient (the economics)

1. **No GPU, no training to ship.** The fast loop is inference-only. You get better answers today with an API key.
2. **Cheap base + targeted spend.** A small model + best-of-N + revise closes most of the gap; escalation buys the frontier only on the hard minority (~72% of always-frontier cost, measured).
3. **Bounded context.** Retrieval is top-K; a token cap and consolidation keep injected context small as memory grows, so cost doesn't creep.
4. **Compounding, not repetition.** A lesson learned once is recalled forever — the second run on a task passes on the first try (pipeline: 12→6 iterations).
5. **Graduation removes ongoing cost.** Once a lesson is baked into weights, it stops costing context tokens per call — same quality, lower steady-state cost.
6. **Portable.** Runs on any OpenAI-compatible model and any vector store; no lock-in, no rebuild.
7. **Free training data.** The costliest input — labeled examples — is a byproduct of use (~2.7 per interaction), not a budget line. The reward does the labeling.
8. **Personalization without fine-tuning.** Per-user quality is a *scope* on the same memory, not a per-user model — near-zero marginal cost per user, and no cold start (they inherit global/team lessons on day one).

---

## Part 5 — References, mapped to our parts

| Area | Systems / papers | Where in ours |
|---|---|---|
| In-context self-improvement | Reflexion, Self-Refine, ExpeL, CRITIC | fast loop, lesson extraction, reward |
| Skills / procedures | Voyager, Agent Workflow Memory | `SkillExtractor` |
| Memory architecture | Generative Agents, Mem0, Zep/Graphiti, A-MEM, MemGPT, CoALA | `SemanticLessonStore` + governance |
| Test-time compute | Self-Consistency, Snell (scaling), Tree-of-Thoughts | amplifiers |
| Weight-level / slow loop | STaR, ReST-EM, FireAct, AgentTuning, GRPO, RULER/ART, RLVR | flywheel, `RestEm`, `Graduation` |
| Failure modes | model collapse/autophagy, MINJA, OWASP Agentic Top-10, reward hacking | verified gating, `guardbench`, `MemoryAudit` |

Full landscape and per-system notes: the four research briefs in the project history; the two-line summary lives on the evidence artifact.

---

## Part 6 — Prepared Q&A (by audience)

> Rule for all answers: lead with the honest core, then the evidence. Never say "always/100%." If it's a mechanism demo, say so.

### The universal hard questions (anyone may ask)

**"Isn't this just Microsoft Agent Framework / Foundry?"**
No — those are the *runtime* (orchestration, tools, hosting, evals). None of them close the loop: score an outcome with a reward and *change future behavior because of it*. Foundry ships Memory, Evaluations and Fine-tune as *separate, manual boxes*; we're the wiring that turns them into a self-improving system. And we run *on* Foundry, even wrap a MAF agent — we complete them, we don't compete.

**"Isn't this just RAG?"**
RAG retrieves *documents* to answer a question. We retrieve *lessons the agent learned from its own mistakes*, scored by a reward, with trust states and a feedback loop that writes new ones. RAG has no reward, no learning, no trust lifecycle. (You can use RAG *and* this — they're orthogonal.)

**"Did you actually measure the cost/quality, or is it a slide?"**
Two headline numbers are measured on a live Foundry model (gpt-4.1-mini vs gpt-5.1) — reproduce with `costbench`/`escbench`. Everything else is a *deterministic* benchmark that exits non-zero if it regresses — run `pwsh scripts/run-evidence.ps1`. We also *walked back* an earlier "it gets cheaper as it learns" claim after rigorous measurement showed injected lessons add prompt tokens — so the numbers you're hearing survived us trying to break them.

**"The compounding-to-weights curve — is the bake real?"**
The *export* and *graduation* code is real, and the `sft.jsonl` it writes is a genuine Foundry fine-tune input. In the offline demo the bake is *simulated* to show the arc end-to-end without a GPU. To make the post-bake column measured, we run one real Foundry fine-tune — that's the single remaining live step, and it's wired.

**"What happens when there are so many lessons they fill the context window?"**
It can't, by design: retrieval returns a bounded top-K (default 3), so context cost is independent of how many lessons exist. On top of that: a token-budget cap, consolidation that folds related lessons into meta-rules, and — when knowledge truly outgrows context — that's the signal to *graduate it into weights*. The ceiling is the handoff to the slow loop, not a failure.

**"Can a bad or malicious lesson poison it?"**
This is our strongest story. The memory is an attack surface Foundry's input guards don't see. We defend the *write* path: injection validation quarantines crafted lessons; a lesson can't earn trust by repeating one case (corroboration across ≥2 distinct situations); a periodic audit re-screens everything. `guardbench` runs four attacks and all fail (15/15), framed against OWASP's Agentic Top-10 and MINJA.

**"Why not just use a bigger model, or wait for cheap models to get good?"**
Two reasons. (1) Cost at scale: a product doing millions of calls can't pay frontier prices for every one; cheap-first + escalate is 72% of always-frontier. (2) A bigger model still starts every conversation from zero — it doesn't *learn your domain from use*. Our layer makes *whatever* model you run improve on *your* tasks, and it's the piece that keeps paying off as models get cheaper.

**"What's actually novel here vs Reflexion / ExpeL / Voyager?"**
The individual mechanisms aren't new — we cite them. What's assembled here and largely absent from the field: a **trust lifecycle** on memory, a **write-path security model** (poisoning defense), **verified-gated** export that avoids model collapse, and a **portable, adoption-first packaging** (one reward, any model, any store, install-as-a-skill). We're honest that it's strong *engineering synthesis* grounded in research, not a new algorithm.

### On personalization (a frequent PO/PM/senior question)

**"Can this personalize per user, or is it one shared brain?"**
Both — and that's the point. Personalization is a *memory scope*: set the user id and lessons learned in their sessions are scoped to them (`user:alice`), while they still inherit team and global lessons. One mechanism, two wins: shared lessons compound for everyone; personal lessons make it theirs. Proven in `personalize` (10/10) — same agent, three users, three different answers, zero leakage.

**"Doesn't per-user personalization mean a cold start for every new user?"**
No — the hierarchy solves it. A brand-new user inherits all global (and their team's) lessons on day one, then personalizes from their own use. There's never an empty-memory void.

**"How is this cheaper than per-user fine-tuning?"**
Fine-tuning a model per user is a training job per user. Here personalization is a *scope filter* on one shared memory — near-zero marginal cost per user, instant, and inspectable/deletable (GDPR-friendly) in a way a per-user model isn't.

**"Privacy — can one user's data leak to another via a shared lesson?"**
Personal lessons are isolated by scope (retrieval never returns another user's `Owner`); only lessons deliberately promoted to global are shared, and those are short *generic rules*, not raw user data. You choose the store and the promotion policy.

### Senior developer
- **"How does the reward stay reliable — isn't LLM-as-judge gameable?"** Prefer deterministic checks; use an LLM critic only as an *extra* signal, never the sole score; if you must judge with a model, use a *different/stronger* one than the generator (self-enhancement bias). We inherit this discipline from the CRITIC / self-reward literature.
- **"Thread-safety / concurrency?"** The stores lock around mutation; the loop is per-request. Cosmos is partitioned by agent. No shared mutable state across requests beyond the store.
- **"Embedding cost?"** One embed per write and per retrieval-situation; retrieval reranks with a cheap model on *summaries*, not full text. Offline it degrades to hit-rate ordering — no network.
- **"How does it compose with our existing framework?"** The harness only cares about the `Reward`; `GenerateAsync` wraps whatever you already call (including a MAF/Foundry agent). It's a layer, not a migration.
- **"Testing?"** Every mechanism has a deterministic bench that exits non-zero on regression; `run-evidence.ps1` is CI-able.

### Junior developer
- **"What do I actually write?"** One class with three methods; the only real work is `Evaluate` — a few `Checks` that score the output. See `docs/ADOPT.md`; it's ~20 lines.
- **"What if I don't have a reward?"** You almost always do: did the code compile, did the JSON parse, did the user accept/edit the answer, does it contain the required fields. Start with one deterministic check and grow it.
- **"Do I need a GPU or to train anything?"** No. It's an API key. Training is an optional later step.
- **"How do I see it learning?"** `outcome.LearnedLessons` after each run; run twice and watch the second run pass on the first try.

### Product Owner (PO)
- **"What's the user value?"** Cheaper answers at higher quality, and an assistant that gets better at *your* domain the more it's used — without a data-science project.
- **"What's the risk to users from a bad lesson?"** Lessons start *Provisional* (tried, not trusted) and only become *Verified* after they demonstrably help across different cases; bad ones decay and are audited out. A human gate can require approval before any lesson is trusted.
- **"Privacy / data?"** Lessons are short, generic rules, not raw user data; you choose the store (including your own DB); nothing leaves your model endpoint. Lessons are inspectable and deletable (unlike weights).
- **"When do we see ROI?"** Immediately on quality (best-of-N + revise), and on cost via escalation; the learning ROI compounds over the first sessions on repeated task types.

### Product Manager (PM)
- **"How is this differentiated / defensible?"** It's the learning layer MAF/Foundry leave open, plus a security angle (write-path defense) nobody ships. Portability (any model, any store, install-as-skill) is the moat.
- **"Effort to production?"** The fast loop is production-ready today; the slow loop needs one Foundry fine-tune to make the weight path measured. Adoption per agent is ~a day (write a reward).
- **"Metrics to track?"** Pass-rate vs a frontier baseline, cost per successful task, iterations-to-pass over sessions, lesson trust mix, escalation rate.
- **"Build vs buy?"** It's built and BCL-only; it wraps what you already bought (Foundry/MAF). No new vendor.

### Tech lead
- **"Ops burden?"** Minimal — a store (a file, Cosmos, or your DB) and the model endpoint. Consolidation/audit are periodic background jobs, not hot-path.
- **"How do we roll it out safely?"** Start with lessons gated behind human approval (Provisional-only injection), watch the trust mix and escalation rate, then relax the gate. The audit sweep and decay are your safety rails.
- **"What skills does the team need?"** C# and the ability to write a reward. No ML background required for the fast loop.
- **"When does it break, and how do we know?"** Reward too weak → learns nothing (visible: no lessons promoted). Reward gameable → watch high-reward/low-quality samples. All benches are CI regression gates.

### CS / AI professional
- **"Relation to continual learning / RL?"** The fast loop is verbal/in-context RL (Reflexion-style); the slow loop is rejection-sampling SFT (ReST-EM/STaR) with GRPO/RULER (ART) as the higher rung. The reward is shared across both.
- **"Catastrophic forgetting?"** Addressed on the slow loop by retrain-from-base + general-data mixing + a regression eval; the lesson store itself is a rehearsal buffer.
- **"Model collapse / autophagy?"** We only export *verified* (reward-passing) trajectories — the documented antidote to training on unfiltered self-output.
- **"Reward hacking?"** Prefer verifiable rewards; corroboration-gated promotion + human gate + decay are structural mitigations; we state plainly that no static reward resists hacking indefinitely, so the reward must be revisable.
- **"Does RL even add skills?"** 2025 evidence says RLVR mostly sharpens base abilities — which is *why we go SFT-first* and keep the fast loop doing the adaptation.
- **"Limitations?"** Quality is bounded by the reward's quality; open-ended tasks with no gradeable signal are hard; the weight-path is wired but the measured bake is the one remaining live step.

### Non-technical (exec / business / curious colleague)
- **"What is a 'growing agent' in one sentence?"** An AI assistant that learns from its mistakes as people use it and gets better over time — without being retrained by engineers.
- **"How does it learn without retraining?"** It keeps a notebook of lessons ("when X happens, do Y") that it wrote from its own corrected mistakes, and it reads the relevant page before answering next time.
- **"Is it safe? Won't it learn wrong things?"** It treats every new lesson as *unproven* until it has actually helped several times, throws out ones that don't, and blocks anything that looks like tampering. A person can require sign-off before any lesson counts.
- **"Why does this matter for us?"** Cheaper AI that fits *our* work better the more we use it, and that we can inspect and correct — instead of a black box we rent by the token.

---

## Part 7 — The honesty ledger (keep this in your head)

| Claim | Status |
|---|---|
| Cheap+harness matches frontier pass rate (10/15 each) | **Measured** on a live Foundry model — reproduce with `costbench` |
| Frontier quality at ~72% cost via escalation | **Measured** — reproduce with `escbench` |
| Poisoning defense (15/15), consolidation (6→2), skill transfer (0→100%), lifecycle (7/7), pipeline (12→6), personalization (10/10) | **Mechanism · deterministic** — offline, self-verifying, CI-gated |
| Flywheel export (SFT/pref/RL) + the data-value meter (lessons/examples mined) | **Real data pipeline** — genuine fine-tune input; example count exact |
| "~$X labeling avoided" | **Estimate** — count is exact, dollar figure is at an assumed $/example rate (shown as "≈"), not revenue |
| The compounding *bake* (context→weights) | **Mechanism demo** offline; the export is real, the actual fine-tune is the one remaining live step |
| ART / GRPO slow loop | **Designed & wired**, not run here — SFT-first is the shipped rung |
| "It gets cheaper as it learns" | **Retired** after measurement (injected lessons add tokens) — we say so, and it's a credibility point |

Lead with the honest core, then the number. That posture is why the pitch holds up under a sharp question.
