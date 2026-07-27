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
_template/                           COPY ME to build a new agent — implement three methods
docs/                                the playbook & pitch one-pagers
```

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
Full recipes + hosting on Azure Container Apps are in [`RUNNING.md`](RUNNING.md).

## Build a new agent

```
cp -r _template sN            # rename csproj, Id ("sN-name"), port, blockKey
# implement three methods in your Agent.cs: GenerateAsync, Evaluate (the reward), LessonFor
dotnet run --project sN
```

See `PATTERN.md §7–§10` for the contract, the five design decisions, and the checklist.

---
*Requires .NET 8 SDK. The agents run offline with deterministic mock models; supply `AGENT_LLM_*` to use
a real model. No secrets are stored in this repo.*
