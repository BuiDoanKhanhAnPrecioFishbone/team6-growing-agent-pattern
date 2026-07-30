# Azure AI Foundry — credentials setup

How to find your endpoint, key, and deployment names, and plug them into the agents. Everything the app
needs is supplied through **environment variables** at runtime — no secrets live in this repo.

> Validated combo (what actually worked for us): the **`*.openai.azure.com`** hostname with **api-key**
> auth. The `*.services.ai.azure.com` endpoint shown on the deployment page is the generic Foundry endpoint
> and did **not** resolve Azure-OpenAI-style calls in our testing — prefer `openai.azure.com`.

---

## 1. What you need (three things)
| Value | Example | Where it comes from |
|-------|---------|---------------------|
| **Endpoint host** | `https://<resource>.openai.azure.com` | Azure portal → your resource → *Keys and Endpoint* |
| **API key** | `xxxx…` (KEY 1 or KEY 2) | same page |
| **Deployment name(s)** | `gpt-4.1-mini`, `text-embedding-3-small` | Foundry portal → your project → *Models + endpoints* |

The **deployment name** is the important subtlety: Azure resolves calls by the *deployment name you chose*,
**not** the base model name (they often match, but not always). It's the value in the **Name** column.

---

## 2. Where to find it in the portals

### A. Deployment names → Azure AI Foundry
`https://ai.azure.com` → pick your **project** → **Build** → **Models + endpoints** (Deployments).
Each row shows the **Name** (use this as the `model` param / URL path), the **model**, and its status.
Click a deployment to see its target endpoint and a code sample.

### B. Endpoint + keys → Azure Portal
`https://portal.azure.com` → open your **Azure AI Services / Cognitive Services** resource →
left menu **Keys and Endpoint**. Copy the **Endpoint** and **KEY 1** (or KEY 2 — either works).
This is also where you **Regenerate** a key (see Security below).

> Tip: from the Foundry deployment page you can also reach the resource via *Manage in Azure portal*.

---

## 3. Set the environment variables

**Chat model** (drives every agent's generation):
```powershell
$env:AGENT_LLM_BASE_URL = "https://<resource>.openai.azure.com/openai/v1"
$env:AGENT_LLM_API_KEY  = "<KEY 1>"
$env:AGENT_LLM_AUTH     = "api-key"          # Azure OpenAI authenticates via the api-key header
$env:AGENT_LLM_MODEL    = "<chat deployment name>"   # e.g. gpt-4.1-mini
```

**Embeddings** (optional — sharpens semantic memory; app falls back to offline if unset/unreachable):
```powershell
$env:AGENT_EMBED_BASE_URL    = "https://<resource>.openai.azure.com/openai/deployments/<embed deployment name>"
$env:AGENT_EMBED_API_KEY     = "<KEY 1>"
$env:AGENT_EMBED_AUTH        = "api-key"
$env:AGENT_EMBED_API_VERSION = "2024-02-01"   # text-embedding-3-* needs the classic route + this version
$env:AGENT_EMBED_MODEL       = "<embed deployment name>"   # e.g. text-embedding-3-small
```

`AGENT_LLM_*` is read per-agent first (`S2_LLM_*`) then falls back to the shared `AGENT_LLM_*`, so setting
the `AGENT_*` ones once configures every agent. Vars last only for the current terminal (use `setx` to persist).

---

## 4. Verify before running the app
```bash
# chat:
curl.exe -s -X POST "https://<resource>.openai.azure.com/openai/v1/chat/completions" \
  -H "api-key: <KEY>" -H "content-type: application/json" \
  -d "{\"model\":\"<chat deployment>\",\"messages\":[{\"role\":\"user\",\"content\":\"ping\"}],\"max_tokens\":5}"

# embeddings:
curl.exe -s -X POST "https://<resource>.openai.azure.com/openai/deployments/<embed deployment>/embeddings?api-version=2024-02-01" \
  -H "api-key: <KEY>" -H "content-type: application/json" -d "{\"input\":\"hi\"}"
```
Expected: a JSON `choices[…]` (chat) / `data[0].embedding[…]` (embeddings).

### Troubleshooting
| Error | Meaning | Fix |
|-------|---------|-----|
| `401 Unauthorized` | wrong/expired key, or key from a different resource | copy the key from *this* resource's *Keys and Endpoint* |
| `DeploymentNotFound` (404) | name/host/api-version mismatch, or a brand-new deployment not yet propagated | check the exact **Name**; use `openai.azure.com`; wait a few min for a fresh *GlobalStandard* deployment |
| `unknown_model` | the `model`/deployment isn't on the host the `/openai/v1` route reached | use the classic `/openai/deployments/<name>/…` route with `api-version` for embeddings |
| `411 Length Required` / broken command | shell split the multi-line curl | run it on **one line** (Windows `cmd` doesn't support `\` continuation) |

---

## 5. Security
- **Never commit keys.** They go in env vars / `dotnet user-secrets` (dev) or **Azure Key Vault + managed
  identity** (prod). This repo stores none.
- **Rotate** on the *Keys and Endpoint* page (**Regenerate Key 1/2**) if a key is ever shared or exposed —
  KEY 1 and KEY 2 exist so you can rotate one while the other stays live.
- Don't paste subscription IDs, tenant IDs, or portal deep-links into public places (this repo included) —
  they're internal identifiers. Keep team-specific links in your internal wiki.
