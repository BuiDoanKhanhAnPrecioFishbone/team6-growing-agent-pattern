# The Growing-Agent Pattern

A solution of **six value-investing agents** that get **better run-to-run** on a hosted LLM
(Azure AI Foundry), cost-optimized, with **no GPU and no model training**. Each agent is a policy in a
deterministic environment, driven by **one reward** that gates it now (and could train it later),
writing a **lesson** to memory after every run so the next run is smarter — and the whole S1→S6
pipeline compounds.

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
_template/                           COPY ME to build a new agent IN THIS REPO — implement three methods
pack.ps1                             pack the harness as NuGet, to build agents in a SEPARATE repo
.claude/skills/build-growing-agent/  the pattern as a skill — coding agents (Claude Code / Codex) follow it
PATTERN.md · RUNNING.md              the playbook (why & the contract) & how to run + wire credentials
docs/
  START-A-NEW-REPO.md                build agents in another repo (NuGet or vendor the harness)
  FOUNDRY-SETUP.md                   find your Foundry endpoint, key & deployment names
  + pitch one-pagers (HTML)
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

Memory backing: set `AGENT_COSMOS_CONNECTION` for Cosmos DB, otherwise a local JSON file is used.
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
[`PLAN-memory-v2.md`](PLAN-memory-v2.md).

- **`SemanticLessonStore`** — embed the situation → vector shortlist → a cheap LLM picks the *applicable*
  lessons → two-phase load. Set `AGENT_EMBED_*` for Azure embeddings; offline it uses a hash embedder.
- **Self-refining writes** (`SemanticLessonStore`): learned lessons start `Provisional` and promote to
  `Verified` on hit-rate or a human gate; injection-validated (suspicious → `Quarantined`, never injected);
  near-duplicates merge instead of piling up.
- **Tools** (`Tools.cs`): agents call `memory_search` (their own memory) and deterministic compute tools via
  a function-calling loop — read-only tools run free, mutating ones gate. `McpToolSource` is the MCP seam.
- **Measured A/B** (`abeval/`): as memory fills with noise, exact-match retrieval collapses toward 0 while
  semantic + recall holds 0.67–1.0. `memtest/` and `tooltest/` verify recall/refine and the tool loop.
- **Second domain / real reward** (`codeagent/`): a code agent whose reward is *unit tests pass* (run in a
  Python subprocess). With learned memory it solves **80% of problems first-try vs 0% without** — real
  end-to-end learning on a deterministic reward, proving the harness is domain-agnostic. Run: `dotnet run --project codeagent`.
- **Status:** D1–2 store · D3–4 recall + two-phase · D5 refine + injection defense · D6–7 A/B chart ·
  D8–10 tool loop + `memory_search` + MCP seam — **all done**. Remaining: robust conflict-check + full MCP transport.

---
*Requires .NET 8 SDK. The agents run offline with deterministic mock models; supply `AGENT_LLM_*` to use
a real model. No secrets are stored in this repo.*
