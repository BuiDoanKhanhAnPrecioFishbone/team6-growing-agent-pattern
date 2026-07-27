# The Growing-Agent Pattern

A small, reusable pattern for building **agents that get better run-to-run** — on a hosted LLM
(Azure AI Foundry), cost-optimized, with **no GPU and no model training required**.

Each agent is a policy inside a deterministic environment, driven by **one reward** that gates it now
(and could train it later), writing a **lesson** to memory after every run so the next run is smarter.
Built for the team6 value-investing analyzer, but the harness is domain-agnostic.

> **Start with [`PATTERN.md`](PATTERN.md)** — the full playbook (why it works, why a Foundry-hosted
> model can't run ART, the contract, the invariants). Then [`RUNNING.md`](RUNNING.md) to run it.

## What's inside

```
shared/AIAssistant.AgentHarness/          the pattern AS CODE (loop + reward contract + memory interface)
shared/AIAssistant.AgentHarness.Cosmos/   Azure-native memory backing (Cosmos DB, partition /agent)
_template/                                 COPY ME to build a new agent — implement three methods
s2/                                        reference implementation: the Moat agent
docs/                                      the playbook & pitch one-pagers (open in a browser)
PATTERN.md · RUNNING.md                    the guide and the run/credentials/hosting doc
```

## Quickstart

```bash
# 1. build everything
dotnet build s2/AIAssistant.S2.Api.csproj

# 2. run the reference agent — OFFLINE (deterministic mock; no credentials needed)
dotnet run --project s2          # → http://localhost:5302

# 3. watch it learn run-to-run
curl -s -X POST localhost:5302/run -H 'content-type: application/json' -d @s2/examples/vnm-input.json
curl -s -X POST localhost:5302/run -H 'content-type: application/json' -d @s2/examples/msn-input.json
curl -s localhost:5302/lessons   # the memory, with hit-rates
```

Run 1 stumbles and fixes a mistake (2 iterations, learns a lesson); run 2 has the lesson injected and
gets it right first try (1 iteration). Fewer iterations = lower cost. That is the whole idea.

## Point it at Azure AI Foundry (no code change)

```powershell
$env:AGENT_LLM_BASE_URL = "https://<your-resource>.openai.azure.com/openai/v1"   # the Azure OpenAI endpoint
$env:AGENT_LLM_API_KEY  = "<key>"
$env:AGENT_LLM_MODEL    = "<your-deployment-name>"
dotnet run --project s2
```

Full recipes (Azure OpenAI classic, local vLLM, Key Vault + managed identity) and **hosting each agent
on Azure Container Apps** are in [`RUNNING.md`](RUNNING.md).

## Build a new agent

```
cp -r _template sN     # rename csproj, Id ("sN-name"), port
# implement three methods: GenerateAsync, Evaluate (the reward), LessonFor
dotnet run --project sN
```

See `PATTERN.md §7–§10` for the contract, the five design decisions, and the checklist.

## Read the visual playbook

`docs/growing-agent-pattern.html` — the team playbook. `docs/compounding-analyst.html` — the pitch
one-pager. Open either in a browser (or host `docs/` with GitHub Pages).

---
*Requires .NET 8 SDK. No secrets are stored in this repo — credentials are supplied via environment
variables at runtime.*
