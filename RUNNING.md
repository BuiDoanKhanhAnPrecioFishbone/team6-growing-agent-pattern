# Running an agent — and giving it Azure AI Foundry credentials

Every growing agent runs in one of two modes:

| Mode | When | Behaviour |
|------|------|-----------|
| **Offline mock** | no `*_LLM_BASE_URL` set | deterministic mock model — the loop + memory still work, great for the demo |
| **Live model** | `*_LLM_BASE_URL` set | calls your Azure AI Foundry deployment for every draft |

No credentials → the agent still runs (mock). Add credentials → it uses the real model. Nothing else changes.

---

## 1. The environment variables

Each agent reads its **own** prefix first (`S2_LLM_*`), then falls back to a **shared** one (`AGENT_LLM_*`).
So you can point *all* agents at one Foundry deployment with the `AGENT_LLM_*` set, or override a single agent.

| Variable (per-agent · shared) | Meaning | Example |
|-------------------------------|---------|---------|
| `S2_LLM_BASE_URL` · `AGENT_LLM_BASE_URL` | the endpoint base (see recipes below) | `https://my-res.openai.azure.com/openai/v1` |
| `S2_LLM_API_KEY` · `AGENT_LLM_API_KEY` | the key from the Foundry deployment | `abc123…` |
| `S2_LLM_MODEL` · `AGENT_LLM_MODEL` | **the deployment name** (Azure) — not the base model id | `gpt-4o-mini` |
| `S2_LLM_AUTH` · `AGENT_LLM_AUTH` | `bearer` (default) or `api-key` (classic Azure OpenAI) | `api-key` |
| `S2_LLM_API_VERSION` · `AGENT_LLM_API_VERSION` | only classic Azure OpenAI needs this | `2024-10-21` |
| `S2_LLM_TEMPERATURE` · `AGENT_LLM_TEMPERATURE` | sampling temperature | `0.4` |

> **Never commit keys.** Use env vars / `dotnet user-secrets` in dev, and Azure Key Vault + a managed
> identity in the cloud (see §4).

---

## 2. Three credential recipes

### A. Azure AI Foundry — OpenAI-compatible v1 route (recommended, simplest)

> **Your Foundry page shows two endpoints — use the "Azure OpenAI endpoint"** (it ends in
> `/openai/v1`), **not** the "Project endpoint" (`…services.ai.azure.com/api/projects/…`). The Project
> endpoint is for the Foundry Agent/Projects SDK — a different API we don't call. We call the model
> directly, which is the OpenAI-compatible route.

Auth is a bearer key; the model is your deployment name. No code change is required.

```bash
export AGENT_LLM_BASE_URL="https://<your-resource>.openai.azure.com/openai/v1"
export AGENT_LLM_API_KEY="<API key from the Foundry page>"
export AGENT_LLM_MODEL="<your-deployment-name>"     # the model/deployment name you created
# AGENT_LLM_AUTH defaults to bearer; no api-version needed on the v1 route
```

The client posts to `…/openai/v1/chat/completions` with `Authorization: Bearer <key>`. If you ever get a
**401**, set `AGENT_LLM_AUTH=api-key` to send the key as the `api-key` header instead.

### B. Classic Azure OpenAI (per-deployment URL)
The base URL includes the deployment path, auth uses the `api-key` header, and an `api-version` is required.

```bash
export S2_LLM_BASE_URL="https://<your-resource>.openai.azure.com/openai/deployments/<deployment>"
export S2_LLM_API_KEY="<azure openai key>"
export S2_LLM_AUTH="api-key"
export S2_LLM_API_VERSION="2024-10-21"
export S2_LLM_MODEL="<deployment>"                  # sent in the body; ignored on per-deployment URLs
```

### C. Any OpenAI-compatible endpoint (local vLLM, OpenRouter, a trained ART checkpoint)
Bearer auth, standard `/chat/completions`. This is also how a **future ART-trained model** is served —
only these vars change, no code does.

```bash
export S2_LLM_BASE_URL="http://localhost:8000/v1"
export S2_LLM_API_KEY="sk-…"        # or omit for a keyless local server
export S2_LLM_MODEL="qwen2.5-7b"
```

The client posts to `{BASE_URL}/chat/completions` with JSON mode on. Set the base URL to the point
where `/chat/completions` is the correct next path segment.

---

## 3. Set the vars, then run

**PowerShell (current session):**
```powershell
$env:S2_LLM_BASE_URL = "https://my-res.openai.azure.com/openai/v1"
$env:S2_LLM_API_KEY  = "…"
$env:S2_LLM_MODEL    = "gpt-4o-mini"
dotnet run --project agents/s2
```

**Dev secrets (never touches source control):**
```bash
cd agents/s2
dotnet user-secrets init
dotnet user-secrets set "S2_LLM_BASE_URL" "https://my-res.openai.azure.com/openai/v1"
dotnet user-secrets set "S2_LLM_API_KEY"  "…"
```

**Verify it's actually using Foundry** — the `/run` response echoes it:
```bash
curl -s -X POST http://localhost:5302/run -H 'content-type: application/json' \
  -d @agents/s2/examples/vnm-input.json | grep -o '"llmEnabled":[a-z]*\|"model":"[^"]*"'
# llmEnabled:true   model:"gpt-4o-mini"     ← live
# llmEnabled:false  model:"mock (offline…)" ← no creds set, running the mock
```

---

## 4. Production: Key Vault + managed identity (no keys in env)

For the deployed platform, don't ship keys at all:

1. Put the Foundry key in **Azure Key Vault**.
2. Give the agent's host (Container App / App Service) a **managed identity** with *Key Vault Secrets User*.
3. Load the secret into `AGENT_LLM_API_KEY` at startup (Key Vault reference in app settings, or the
   Azure SDK). The agent code is unchanged — it still just reads the env var.
4. Even better, use **Entra ID** auth to Foundry (a bearer token from the managed identity instead of a
   key). That's a small extension to `ChatClient` (swap the static key for a `DefaultAzureCredential`
   token) — noted as a follow-up; the key path above works today.

---

## 5. The same pattern for every agent

`S3_LLM_*`, `S4_LLM_*`, … follow the identical convention, or set `AGENT_LLM_*` once for all of them.
Memory backing is chosen the same way: set `AGENT_COSMOS_CONNECTION` for Cosmos, otherwise a local JSON
file is used (see `PATTERN.md` §6).

---

## 6. Embeddings (Memory v2 semantic retrieval)

Semantic retrieval shortlists lessons with embeddings. Set `AGENT_EMBED_*` to use an Azure AI Foundry
embedding deployment; with nothing set it uses a deterministic **offline hash embedder** (good enough for
dev/demo — the retrieval A/B still holds, just capped below ~1.0). The embedder is **fail-safe**: if the
endpoint is unreachable it silently degrades to the offline embedder rather than breaking retrieval.

`AGENT_EMBED_BASE_URL` is the deployment path **without** the trailing `/embeddings` (the client appends it):

```powershell
# Azure OpenAI classic deployment route. NOTE: use the *.openai.azure.com hostname, NOT the
# *.services.ai.azure.com endpoint the deployment page shows — and api-version 2024-02-01 for text-embedding-3.
$env:AGENT_EMBED_BASE_URL   = "https://<resource>.openai.azure.com/openai/deployments/text-embedding-3-small"
$env:AGENT_EMBED_API_KEY    = "<key>"
$env:AGENT_EMBED_AUTH       = "api-key"
$env:AGENT_EMBED_API_VERSION= "2024-02-01"
$env:AGENT_EMBED_MODEL      = "text-embedding-3-small"
```

> If a fresh GlobalStandard deployment returns `DeploymentNotFound` from one network but works from another,
> it's edge propagation — wait a few minutes or run from the network where your portal call succeeded.

Verify the endpoint resolves from your environment first:

```bash
curl -s -X POST "$AGENT_EMBED_BASE_URL/embeddings?api-version=2024-10-21" \
  -H "api-key: $AGENT_EMBED_API_KEY" -H "content-type: application/json" -d '{"input":"hi"}'
```

A `DeploymentNotFound` here usually means the key belongs to a *different* resource than the one hosting the
deployment, or the endpoint hasn't propagated to your network edge yet — not a code issue.
