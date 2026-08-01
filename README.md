# The Growing-Agent Pattern

A reusable .NET harness that makes a **cheap** LLM (Azure AI Foundry `gpt-4.1-mini`) perform like a far
more expensive one — **no GPU, no fine-tuning** — through inference-time scaffolding and a memory that
**grows run-to-run**. An agent is a policy in a deterministic environment, driven by **one reward** that
gates it now (and could train it later); every mistake the loop fixes becomes a **lesson** the next run
recalls, so the whole pipeline compounds. The reference domain is a six-step value-investing pipeline
(S1→S6), but the harness knows nothing about finance — swap the reward and it grows a **code agent** just
as well.

**Results — each is a program you can run** ([one-page overview](docs/harness-results.html)):
code solved first-try **80% with memory vs 0% without** · grounding **5/6 → 6/6** · retrieval precision
**1.000** as memory fills with noise · **13** live MCP tools · context compaction recalls a fact naive
truncation forgets.

> **Start with [`PATTERN.md`](PATTERN.md)** — the playbook (why it works, why Foundry can't run ART,
> the contract, the invariants). Then [`RUNNING.md`](RUNNING.md) to run it and wire credentials.

## Solution layout (`GrowingAgentPattern.slnx`)

```
shared/
  AIAssistant.AgentHarness/          the pattern AS CODE (fast loop + reward contract + memory interface)
  AIAssistant.AgentHarness.Cosmos/   Azure-native memory backing (Cosmos DB, partition /agent)
  AIAssistant.AgentHost/             one-line HTTP host + authoring helpers, so each agent is ~3 files
s1-screen/  s2-moat/  s3-financials/  s4-valuation/  s5-allocate/  s6-monitor/
                                     the SIX agents — each its own runnable service (ports 5301–5306)
orchestrator/                        runs S1→S6 over one candidate file, auto-confirming the 4 human gates
compare/                             DEMO — same model, bare (playground) vs harness, side by side, any domain
_template/                           COPY ME to build a new agent IN THIS REPO — implement three methods
pack.ps1                             pack the harness as NuGet, to build agents in a SEPARATE repo
.claude/skills/build-growing-agent/  the pattern as a skill — coding agents (Claude Code / Codex) follow it
PATTERN.md · RUNNING.md              the playbook (why & the contract) & how to run + wire credentials
docs/
  PITCH.md                           the pitch: submission narrative + 5-min demo run-of-show + Q&A
  pitch-deck.html                    presentation deck (open in a browser)
  harness-results.html               one-page results overview
  START-A-NEW-REPO.md                build agents in another repo (NuGet or vendor the harness)
  COSMOS-MEMORY.md                   persist lessons/context/memory in Cosmos DB (vector search)
  FOUNDRY-SETUP.md                   find your Foundry endpoint, key & deployment names
```

**Three ways to build a new agent** — inside this repo (`_template`), in a separate repo
([`docs/START-A-NEW-REPO.md`](docs/START-A-NEW-REPO.md)), or by letting a coding agent follow the
[`build-growing-agent`](.claude/skills/build-growing-agent/) skill. See [Build a new agent](#build-a-new-agent) below.

Every agent implements the same three-method `IAgent` contract on the shared harness. The loop, memory
and reward-shape are written **once** and never re-implemented per agent.

## Quickstart

```bash
dotnet build GrowingAgentPattern.slnx

# Run the whole pipeline (offline mock — no credentials needed), twice, to see it compound:
dotnet run --project orchestrator -- --fresh
```

Run 1 (VNM): every agent stumbles once on its own learnable flaw, fixes it, writes a lesson → 12 iters.
Run 2 (MSN, same industry): each agent has its lesson injected and gets it right first try → **6 iters**.
Fewer iterations = lower cost, pipeline-wide. Ends with a full recommendation (BUY, size, entry, monitor).

### Every proof, one command each

Each capability is backed by a runnable program. Offline ones need nothing; live ones need `AGENT_LLM_*`
set to your Foundry deployment (see [`docs/FOUNDRY-SETUP.md`](docs/FOUNDRY-SETUP.md)).

```bash
# offline · deterministic
dotnet run --project orchestrator -- --fresh   # the compounding pipeline (12 → 6 iters)
dotnet run --project abeval                     # retrieval vs noise: semantic holds, exact-match collapses
dotnet run --project codeagent                  # 2nd domain, real reward = unit tests (80% vs 0%)
dotnet run --project mcptest                     # 13 real MCP tools over stdio (needs node)
dotnet run --project memlife                     # memory lifecycle: decay/eviction/conflict — 7/7
dotnet run --project ctxtest                     # context management — 11/11
dotnet run --project flywheel                    # ART flywheel: runs → SFT/preference/RL training corpus

# live · set AGENT_LLM_* first
dotnet run --project ampeval                     # grounding: bare 5/6 → +web_search 6/6
dotnet run --project ctxdemo                     # context: compaction recalls, truncation forgets
dotnet run --project costbench                    # quality & $ head-to-head: mini vs mini+harness vs frontier
dotnet run --project escbench                     # cost-optimized: escalation — frontier quality at ~72% cost
```

**The cost thesis** (`costbench/`, `escbench/`): measured on hard reasoning traps with a `CostLedger` that
records every call's token `usage`. **Quality** — mini+harness matches the frontier (**10/15 = gpt-5.1's
10/15**; even gpt-5.1 solves only 10). **Cost** — the honest lever is **escalation**: run mini+harness and let
the reward escalate to gpt-5.1 *only* on the tasks it can't crack → frontier quality at **~72% of
always-frontier cost**, paying the premium on just **6/15**. (We measured and *retired* an earlier
"cheaper as it learns" claim once the rigorous run didn't support it — the standing claim is escalation.)

### Run a single agent as its own service

```bash
dotnet run --project s1-screen          # → http://localhost:5301
curl -s localhost:5301/                 # {"service":"s1-screen","block":"screen","status":"up"}
curl -s -X POST localhost:5301/run -H 'content-type: application/json' \
  -d '{"ticker":"VNM","industry":"consumer_staples","sources":["AR2025 p.12"]}'
curl -s localhost:5301/lessons          # what this agent has learned, with hit-rates
```

Ports: s1 5301 · s2 5302 · s3 5303 · s4 5304 · s5 5305 · s6 5306.

### Watch the flow in a UI

```bash
dotnet run --project ui          # → http://localhost:5300
```

A control panel that runs the pipeline, shows **each agent's result** (block JSON + telemetry + gate),
and lets you **evaluate & teach** — reject an agent's output and state the rule it must follow; your
feedback becomes a lesson the agent applies on the next run (and the ART training corpus later). Reset
memory and re-run to watch the whole pipeline compound. The header shows **● LIVE · &lt;model&gt;** when a
Foundry endpoint is configured, or **○ mock (offline)** otherwise.

## The demo — harness vs the playground

The single screen that makes the case, across **any** domain:

```bash
# set AGENT_LLM_* to your Foundry gpt-4.1-mini first (this demo is live)
dotnet run --project compare          # → http://localhost:5310
```

Same model on both sides. **Left** is one raw completion — exactly the Foundry playground. **Right** runs
that *same model* through the real harness: a reward-gated loop with a growing memory. Pick a domain
(**UI-from-a-design · Factual QA · General reasoning · Value-investing**), hit **Run**, then **Run again** to
watch the harness side compound while the playground stays flat:

- **UI from a design** — reproduce a component derived from a Figma frame (a *Review-for-Candidate* card:
  titled header, star rating, review textarea, two buttons, violet theme). Bare one-shots a thin, partial
  card; the harness's reward checks the spec (every element + rounded / shadow / responsive), **iterates to a
  complete card**, and learns it. Both results render **side by side in live preview frames** — you see the
  difference. (The committed brief is structure/style only; point it at your own Figma via the MCP for a live
  target.)
- **Factual QA** — bare answers from stale memory (*"Warren Buffett"*); the harness's reward fails it, it
  **learns to call `web_search`**, answers *"Greg Abel"* — and recalls that lesson first-try next run.
- **General reasoning** — bare rushes a trick question wrong; **best-of-N + self-verify** work it correctly.
- **Value-investing** — bare drops a citation; the grounding gate catches it and it **learns to cite only
  provided sources**.

To keep it honest, "bare" is the harness's *own* first draft with the loop and memory switched off — the
only variable is the harness. A one-page overview of all results lives at
[`docs/harness-results.html`](docs/harness-results.html).

## The six agents

| S | Agent | Human gate | Grounding gate → learnable trigger |
|---|-------|-----------|-------------------------------------|
| 1 | Screen | #1 shortlist | echo enforced criteria → `MISSING_CRITERIA` |
| 2 | Moat | #2 strength | cite only provided sources → `UNCITED_SOURCE` |
| 3 | Financials | — | surface fired red flags → `MISSING_REDFLAG` |
| 4 | Valuation | #3 assumptions | type every assumption (needs confirmed moat) → `UNTYPED_ASSUMPTION` |
| 5 | Allocate | #4 buy/size | a buy must carry the disclaimer → `MISSING_DISCLAIMER` |
| 6 | Monitor | act on alerts | every alert cited → `UNSOURCED_ALERT` |

## Point it at Azure AI Foundry

```powershell
$env:AGENT_LLM_BASE_URL = "https://<your-resource>.openai.azure.com/openai/v1"   # the Azure OpenAI endpoint
$env:AGENT_LLM_API_KEY  = "<key>"
$env:AGENT_LLM_MODEL    = "<your-deployment-name>"
```

These vars wire **every agent, the orchestrator and the UI** to your model — set them, then run any of
them. With nothing set, everything runs a deterministic mock (offline). If the endpoint is unreachable or
the key is wrong, each agent degrades to its mock draft rather than failing, so the flow never breaks.

Memory backing: set `AGENT_COSMOS_CONNECTION` for Cosmos DB (add `AGENT_COSMOS_VECTOR=1` for **server-side
vector search** — semantic recall in the cloud), otherwise a local JSON file is used. Teammates: the
copy-this guide to persist your agent's lessons/context/memory is [`docs/COSMOS-MEMORY.md`](docs/COSMOS-MEMORY.md).
Full recipes + hosting on Azure Container Apps are in [`RUNNING.md`](RUNNING.md). **New to Foundry —
finding your endpoint, key, and deployment names in the portal?** See [`docs/FOUNDRY-SETUP.md`](docs/FOUNDRY-SETUP.md).

## Build a new agent

```
cp -r _template sN            # rename csproj, Id ("sN-name"), port, blockKey
# implement three methods in your Agent.cs: GenerateAsync, Evaluate (the reward), LessonFor
dotnet run --project sN
```

See `PATTERN.md §7–§10` for the contract, the five design decisions, and the checklist.

**Building agents in a *separate* repo?** The harness ships as NuGet packages (`./pack.ps1`) so another
repo can consume it — see [`docs/START-A-NEW-REPO.md`](docs/START-A-NEW-REPO.md) for the NuGet and
vendor paths, and how teammates stay on one shared harness version.

**Using an AI coding agent (Claude Code / Codex)?** Invoke the **`build-growing-agent`** skill
(`.claude/skills/build-growing-agent/`) — it walks the coding agent through the same pattern and guardrails,
so new agents come out consistent every time. It's the *executable* form of `PATTERN.md`.

- **Humans** read `PATTERN.md` and copy `_template`.
- **Coding agents** follow the skill.
- **When the harness changes:** update `PATTERN.md` + the skill together, commit; teammates re-sync their
  skills folder and every coding agent scaffolds to the new pattern automatically. (Symlink the skill into
  `.codex/skills/` etc. to share it across other agent runtimes — the team6 shared-skills convention.)

## Memory v2 — semantic, self-refining lessons (in progress)

The lesson memory is being upgraded from exact-match to **semantic retrieval with LLM recall** plus
self-refinement — behind the same `ILessonStore` seam, so agents don't change. Plan &amp; schedule:
[`docs/PLAN-memory-v2.md`](docs/PLAN-memory-v2.md).

- **`SemanticLessonStore`** — embed the situation → vector shortlist → a cheap LLM picks the *applicable*
  lessons → two-phase load. Set `AGENT_EMBED_*` for Azure embeddings; offline it uses a hash embedder.
- **Self-refining writes** (`SemanticLessonStore`): learned lessons start `Provisional` and promote to
  `Verified` on hit-rate or a human gate; injection-validated (suspicious → `Quarantined`, never injected);
  near-duplicates merge instead of piling up.
- **Self-curating lifecycle** (`SemanticLessonStore`): the memory decays stale lessons in ranking
  (`AGENT_MEMORY_HALFLIFE_DAYS`), evicts the least-valuable over a per-agent cap (`AGENT_MEMORY_CAP`), and
  demotes a Verified rule that gets re-learned with conflicting guidance (+ optional LLM contradiction check).
  All off by default. Verified by `memlife/` (`dotnet run --project memlife` — 7 deterministic checks).
- **Tools** (`Tools.cs`, `Mcp.cs`): agents call `web_search` (keyless Wikipedia), `memory_search` (their own
  memory), deterministic compute tools, **and any MCP server's tools** — `McpToolSource` is a real MCP client
  (stdio JSON-RPC: connect → `tools/list` → each wrapped as a gated `ITool`). All run in one function-calling
  loop; read-only tools run free, mutating ones gate. Verified live by `mcptest/`.
- **Measured A/B** (`abeval/`): as memory fills with noise, exact-match retrieval collapses toward 0 while
  semantic + recall holds 0.67–1.0. `memtest/` and `tooltest/` verify recall/refine and the tool loop.
- **Second domain / real reward** (`codeagent/`): a code agent whose reward is *unit tests pass* (run in a
  Python subprocess). With learned memory it solves **80% of problems first-try vs 0% without** — real
  end-to-end learning on a deterministic reward, proving the harness is domain-agnostic. Run: `dotnet run --project codeagent`.
- **Status:** D1–2 store · D3–4 recall + two-phase · D5 refine + injection defense · D6–7 A/B chart ·
  D8–10 tool loop + `memory_search` · **real MCP transport (stdio)** · **memory lifecycle (decay + eviction +
  conflict)** · **context management** — all done. 🎉

## The amplifier — a cheap model, frontier-ish quality

The harness is **inference-time compute**: spend a little more structured thinking per task so a cheap model
(gpt-4.1-mini) reaches quality that usually needs a bigger model — the cost-optimized bet. The levers are all
in the harness (domain-agnostic; every agent inherits them), non-breaking, and composable:

- **Best-of-N** (`HarnessOptions.Samples` / `AGENT_SAMPLES`, default 1): the loop draws N independent drafts
  per round and keeps the one the **reward** scores highest. `HarnessOutcome.Generations` reports the spend.
- **Web-search grounding** (`WebSearchTool`): the model looks facts up instead of guessing — keyless
  (Wikipedia) by default, a keyed provider drops in at `WebSearch.FromEnvironment`.
- **Self-verify** (`AGENT_SELF_VERIFY=1`): an LLM critic reviews each draft and can force another revision
  for soft errors the deterministic reward can't see — the reward still owns scoring.
- **Model cascade** (`AGENT_LLM_MODEL_STRONG=<deployment>`): the loop runs the cheap model, and escalates to
  the bigger deployment **only** when it finishes below threshold — pay for the frontier model on hard cases only.

Every lever is off by default and inert without a live model, so the offline pipeline is byte-for-byte unchanged.

**Proof** (`ampeval/`): the same cheap model answers a factual suite twice — bare, then +`web_search` —
printing an accuracy table. Live-only: set `AGENT_LLM_*` then `dotnet run --project ampeval`.

## Context management — bounded long sessions

Two timescales of memory. The lesson store is **long-term** (facts kept across runs); a long *session* also
has **short-term** memory — the conversation and tool output the model holds at once, which overflows the
window and runs up cost if left unmanaged. `Context` (+ `ContextBudget`, on via `AGENT_CONTEXT_TOKENS`)
compacts it the way Claude Code does — recent detail sharp, older detail gist:

- **`Context.FitAsync`** — compact a conversation to a token budget (keep system + last *N* turns; fold the
  rest into one summary — LLM if configured, deterministic digest offline).
- **`Context.CompactToolHistory`** — bound a live tool-loop history in place, trimming the oldest tool
  results while keeping tool-call pairing intact. Wired into `ToolLoop`.

Off by default. Verified by `ctxtest/` (`dotnet run --project ctxtest` — 11 deterministic checks).

**Live demo** (`ctxdemo/`): a needle-in-a-haystack — plant a client fact early in a long session, bury it
under filler, ask about it at the end under a tight budget. At the **same** budget, naive truncation forgets
the fact; compaction's summary remembers it. Live-only: set `AGENT_LLM_*` then `dotnet run --project ctxdemo`.

---
*Requires .NET 8 SDK. The agents run offline with deterministic mock models; supply `AGENT_LLM_*` to use
a real model. No secrets are stored in this repo.*
