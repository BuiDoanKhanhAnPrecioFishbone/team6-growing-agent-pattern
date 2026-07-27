# The Compounding Analyst — pitch one-pager

> The repo-readable version of the pitch. The full playbook is [`../PATTERN.md`](../PATTERN.md).

**We don't ship prompts. We grow analysts inside a harness whose edge compounds run over run.**

> Investing is too large to one-shot. So each analysis step is an **agent** living in a deterministic
> financial environment it cannot fabricate — with tools to act, a knowledge-graph memory of every
> company, thesis, and lesson, and **one reward** that gates it today and could train it tomorrow.
> The agent grows. The cost goes down.

## Two-speed learning over a single reward

```
                     ONE REWARD  ·  reward(output, environment)
                     gate NOW  ·  training signal LATER
      ┌───────────────────────────────┬───────────────────────────────┐
      │ FAST LOOP (our pattern)        │ SLOW LOOP (ART — optional)     │
      │ generate → evaluate → retrieve │ pooled high-score traces →     │
      │ lesson → revise → pick best →  │ GRPO → better weights →        │
      │ write lesson                   │ redeploy to Foundry            │
      │ ▲ improves within a run +      │ ▲ needs GPU + open weights     │
      │   run-to-run via memory        │   — architecture-ready, not    │
      │   (Foundry-only, no GPU)       │   required                     │
      └───────────────────────────────┴───────────────────────────────┘
```

Both loops optimize the **same** reward — so every run we serve is also data that makes the next better.

## The eight harness primitives (why the engineering is mature)

| # | Primitive | The signal |
|---|-----------|------------|
| 1 | Uniform agent contract | six agents, one shape |
| 2 | Environment / tools split | the agent can't fabricate a fact — grounding by construction |
| 3 | Reward = gate = training signal | one evaluator, triple duty |
| 4 | Two-speed learning | reflect now, train later — ART is an upgrade, not a dependency |
| 5 | Hit-rated memory | lessons self-correct; bad notes decay out |
| 6 | Cost in the harness | cascade, cache, offload, retrieval budget |
| 7 | Gates as typed interrupts | human-in-the-loop is structural |
| 8 | Trace → training corpus | the memory *is* the dataset |

## The proof (S2 Moat, offline, fresh memory)

```
RUN 1 · VNM   iterations 2 · firstScore 0 (invented a citation → gate) · best 1.0 · LEARNED a lesson
RUN 2 · MSN   iterations 1 · firstScore 1.0 (lesson injected up front) · best 1.0 · hitRate → 1.0
```

Run 1 stumbles and fixes it; run 2 never stumbles. **Fewer iterations = lower token cost.** Smarter
*and* cheaper — with no weight training. That is the whole thesis, running.

---
*Tech scope: one LLM from Azure AI Foundry, cost-optimized. See [`../RUNNING.md`](../RUNNING.md) to run it.*
