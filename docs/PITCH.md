# Pitch — The Growing-Agent Pattern · team6 · Omnia-PF-Hackathon-2026

## The one line
> **A cheap model plus a good harness performs like a frontier one — and gets *cheaper* as it learns.**

Everything below is backed by a program you can run in the repo.

---

## 1. The written submission (the narrative)

**The problem.** Frontier LLMs are powerful but expensive, and their cost is **flat forever** — the 10,000th
call costs the same as the first. Cheap models are affordable but weak. An enterprise product like **Omnia**
doesn't run one agent; it runs *many* — screening, drafting, reviewing, summarizing — where cost and quality
both matter at scale.

**Our bet.** Don't reach for a bigger model. Build a **harness** that makes a *cheap* model punch above its
weight, and **remembers what it learns** so it improves run-to-run. No GPU, no fine-tuning, on a plain Azure
AI Foundry deployment — exactly the contest's cost-optimized constraint.

**What we built — the Growing-Agent harness.** An agent is a *policy* in a deterministic environment, judged
by **one reward** that gates it now (and could train it later). Around that, a reusable, domain-agnostic
substrate:
- **The amplifier** (inference-time compute): best-of-N sampling, web-search grounding, LLM self-verify, and
  escalation — levers that lift a weak model toward frontier quality.
- **Memory at two timescales:** a long-term, self-curating **lesson store** (semantic recall, earned trust,
  decay/eviction/conflict handling) and short-term **context compaction** for long sessions.
- **Tools:** `web_search`, `memory_search`, deterministic compute, **vision**, and **any MCP server** (a real
  stdio client) — so an agent can ground itself in facts, its own memory, or a design.
- **One contract** (`IAgent`: three methods). Swap the reward and the *same* harness grows a value-investing
  agent, a code agent, or a UI-from-design agent — proven across four domains.

**The proof (measured, not asserted).**
| Claim | Result | Program |
|---|---|---|
| Cheap model + harness ≈ **frontier** | mini+harness **4/5 = gpt-5.1 4/5**, at ~**79%** of gpt-5.1's cost *and dropping as it learns* | `costbench` |
| Learns on a **real reward** | code solved first-try **80% with memory vs 0% without** | `codeagent` |
| Memory that **scales** | semantic retrieval **1.000** while exact-match collapses to 0 as noise grows | `abeval` |
| **Grounding** beats guessing | bare **5/6 → 6/6** with `web_search` (fixed a post-cutoff fact) | `ampeval` |
| **Context** under budget | compaction recalls a fact; naive truncation *confabulates* a wrong one | `ctxdemo` |
| Real **MCP** tools | **13** tools discovered & called live over stdio | `mcptest` |
| **Training-ready** | every run exports SFT / preference / RL data | `flywheel` |

**Why it matters for Precio Fishbone / Omnia.**
- **Cheaper AI at scale** — run gpt-4.1-mini everywhere, reach frontier quality only where a task needs it,
  and get *cheaper over time* as agents learn. Frontier models never do that.
- **Agents that learn from your experts** — a reviewer rejecting an answer and stating the rule becomes a
  durable, trusted lesson the agent applies next time. Knowledge work compounds.
- **Deployable today** — plain Foundry endpoint; Cosmos DB backing with server-side vector search; the
  pattern is *followable* by the team (a `dotnet new`-style skill + NuGet packages + docs).
- **Future-proof** — every run is a labeled dataset; the day a GPU budget appears, we fine-tune (ART/GRPO/DPO)
  with zero rework.

**The close.** We didn't build a demo of one clever agent. We built the **reusable substrate** that makes
*any* cheap-model agent grow — proven, measured, and cost-optimized on Azure Foundry.

---

## 2. The 5-minute demo run-of-show

> Setup: `dotnet run --project compare` with `AGENT_LLM_*` + `AGENT_LLM_MODEL_STRONG=gpt-5.1` set →
> `http://localhost:5310`. Have the UI tab open. Same `gpt-4.1-mini` powers **both** panels the whole time.

**[0:00 · 20s] Frame it.**
"Same cheap model on both sides. Left is one raw completion — the Foundry playground. Right is that *same
model* through our harness. Watch what the harness adds."

**[0:20 · 90s] UI from a design — grounding + learning (the visual wow).**
- Point to the **Target (Figma)** card at the top. Click **Run**.
- "Playground: a generic, incomplete card. Harness: it grounded the model in the *actual design*, learned the
  exact tokens — violet #6f42c1, Poppins, gold stars, the right buttons — and even made the stars clickable
  and Save disabled." Show the **lessons it learned** below.
- Click **Run again**: "Now it *recalls* those lessons — first try."

**[1:50 · 70s] It learns and compounds (any domain).**
- Switch to **Factual QA** → the *Berkshire 2026 CEO* task → **Run**.
- "Playground: 'Warren Buffett' — confidently wrong, its training is stale. Harness: the reward fails it, it
  **learns to call web_search**, answers 'Greg Abel'." Click **Run again** → "recalled, right first try."
- One line: "Same machinery, different reward — finance, code, reasoning, UI. It's *domain-agnostic*."

**[3:00 · 90s] The money slide — cost.**
- Scroll to the **Cost thesis** panel. Click **Measure cost**.
- Read the table: "Same reasoning problems. Bare mini is cheap but wrong. Our harness **matches gpt-5.1's
  quality** — and while gpt-5.1 costs the same every single time, **we get cheaper as we learn**."
- The line: "Frontier quality, a fraction of the cost, and the cost keeps *falling*."

**[4:30 · 30s] Close + the future.**
"No GPU, no fine-tuning, on Azure Foundry today. And every run we just did quietly exported training data —
the moment a GPU appears, we're already ART-ready. That's the growing-agent harness."

*(Fallbacks: if a live run is slow or a model already knows an answer, switch to the Reasoning tab or note the
offline proofs — `dotnet run --project flywheel` / `orchestrator -- --fresh` always work.)*

---

## 3. Anticipated questions (have answers ready)

- **"Isn't this just prompt engineering?"** No — it's a *reward-gated loop with persistent memory*. The
  reward is deterministic and unhackable; the memory is curated (trust, decay, conflict). Prompting doesn't
  compound run-to-run; this does.
- **"You gave the harness the design/tools but not the playground — unfair?"** That's the point: the harness's
  value *is* grounding + memory + tools. The realistic comparison is "a dev with the harness" vs "a dev in the
  playground." Same model, same task.
- **"100% match to Figma?"** No — we report *design-elements matched* (a checklist) and are explicit it's not
  pixel-identical. For pixel-perfect production code, that's Figma Code Connect — a different, deterministic
  tool. The harness is for *learning*, not exact export.
- **"Small sample on the cost number?"** Yes (n=5 in the console) — directionally strong; a bigger suite
  firms it. Token counts are exact; prices are configurable to your real Azure rates.
- **"Why not just use the frontier model?"** Cost that never drops, and a hard dependency. Ours reaches the
  same quality on a cheap model, gets cheaper with use, and degrades gracefully offline.

---

## 4. Links
- **Repo:** `github.com/BuiDoanKhanhAnPrecioFishbone/team6-growing-agent-pattern`
- **One-page results:** [`docs/harness-results.html`](harness-results.html)
- **The playbook:** [`PATTERN.md`](../PATTERN.md) · **Run it:** [`RUNNING.md`](../RUNNING.md)
