# Persist your agent's memory in Cosmos DB (with embeddings + vector search)

This is the "copy-this" backing for teammates: how to save your agent's **lessons, context, and memory** in
Azure Cosmos DB with **semantic (vector) retrieval** — so an agent you build keeps what it learns across
runs, in the cloud, and recalls it by meaning. It's the Azure-native form of `SemanticLessonStore`, behind
the same `ILessonStore` seam, so **no agent code changes** — you set env vars and it's wired.

> Two timescales of memory (see `PATTERN.md §6`): **long-term lessons** (this doc) and **short-term session
> context** (`Context` compaction). This doc covers the long-term store; session context is compacted at
> runtime and, if you want it persisted, is written as lesson-shaped notes the same way.

---

## 1. What gets stored

One container, **partition key `/agent`** — each agent's memory is its own partition, so scoped reads stay
cheap and isolated while all agents share the container. One document per lesson:

| field | meaning |
|-------|---------|
| `id` | `"{agent}\|{sector}\|{trigger}"` — the upsert key |
| `agent` | partition key (e.g. `s2-moat`) |
| `sector`, `trigger`, `condition` | scope + when-it-applies |
| `warning` | the guidance injected into the next generation |
| `embedding` | `float[1536]` — the vector of *(condition + warning)*, used for search |
| `trust` | `Provisional` / `Verified` / `Quarantined` |
| `timesApplied`, `timesHelped`, `hitRate`, `date`, `lastUsed` | self-correction stats |

Retrieval embeds the current **situation** and ranks by `VectorDistance` over a **DiskANN** vector index on
`/embedding`; with no situation it falls back to hit-rate ordering (drop-in with the exact-match store).

---

## 2. One-time Azure setup

**a. Enable NoSQL vector search on the Cosmos account** (required — the vector index can't be created
without it):
```bash
az cosmosdb update -g <resource-group> -n <account> --capabilities EnableNoSQLVectorSearch
```

**b. Have an embeddings deployment** — `text-embedding-3-small` on your Foundry/Azure OpenAI resource
(1536 dims). See [`FOUNDRY-SETUP.md`](FOUNDRY-SETUP.md).

You do **not** need to create the container by hand — the store creates it on first use *with the vector
embedding policy and DiskANN index* (both are set at creation time and can't be added later, so let the
store make a fresh one).

---

## 3. Wire it (env vars only)

```powershell
# where the lessons live
$env:AGENT_COSMOS_CONNECTION = "<cosmos connection string>"
$env:AGENT_COSMOS_DB         = "team6"        # optional (default team6)
$env:AGENT_COSMOS_CONTAINER  = "lessons"      # optional (default lessons)
$env:AGENT_COSMOS_VECTOR     = "1"            # turn ON semantic (vector) retrieval

# embeddings the vectors are built from (must be 1536-dim to match the container policy)
$env:AGENT_EMBED_BASE_URL    = "https://<resource>.openai.azure.com/openai/deployments/text-embedding-3-small"
$env:AGENT_EMBED_API_KEY     = "<key>"
$env:AGENT_EMBED_MODEL       = "text-embedding-3-small"
$env:AGENT_EMBED_API_VERSION = "2024-02-01"
$env:AGENT_EMBED_AUTH        = "api-key"
```

Then run any agent as usual — `Host.Run(...)` picks the store from the environment:

- `AGENT_COSMOS_CONNECTION` **+** `AGENT_COSMOS_VECTOR=1` → **`CosmosSemanticLessonStore`** (vector search)
- `AGENT_COSMOS_CONNECTION` only → `CosmosLessonStore` (exact-match, no embeddings)
- neither → local `SemanticLessonStore` (a JSON file — the offline default)

> Without `AGENT_EMBED_*`, embeddings fall back to the offline hash embedder (1024-dim), which **won't match**
> the 1536-dim container policy — set the embeddings env vars whenever `AGENT_COSMOS_VECTOR=1`.

---

## 4. Use it directly (building a new agent)

Nothing in your `IAgent` changes. If you construct the store yourself instead of via `Host`:

```csharp
using AIAssistant.Harness;
using AIAssistant.Harness.Cosmos;

ILessonStore store = new CosmosSemanticLessonStore(
    Environment.GetEnvironmentVariable("AGENT_COSMOS_CONNECTION")!,
    database: "team6", container: "lessons");   // embedder defaults to Embeddings.FromEnvironment()

var harness = new AgentHarness(store);
await harness.RunAsync(myAgent, ctx, HarnessOptions.FromEnvironment(), ct);
```

The harness writes a lesson after every fixed mistake and recalls the relevant ones next run — now durable in
Cosmos and searchable by meaning.

---

## 5. Checklist for a teammate

- [ ] `EnableNoSQLVectorSearch` on the Cosmos account
- [ ] `text-embedding-3-small` deployed; `AGENT_EMBED_*` set (1536-dim)
- [ ] `AGENT_COSMOS_CONNECTION` + `AGENT_COSMOS_VECTOR=1` set
- [ ] run your agent once → the `lessons` container is auto-created with the vector policy
- [ ] `GET /lessons` (or `AllAsync`) to see what it has learned, with hit-rates and trust
- [ ] reads/writes stay in your agent's `/agent` partition — other agents' memory is untouched

---

## 6. Extending it

- **Dedup / decay / eviction** — the in-memory `SemanticLessonStore` also merges near-duplicates and evicts
  stale lessons (`PATTERN.md §6`); the same policies port to Cosmos as a vector-neighbour query on write and
  a scheduled cleanup — layer them in `CosmosSemanticLessonStore.WriteAsync` when your memory grows large.
- **Tier-1 domain graph** — companies/statements/theses (the shared knowledge graph) belong in their own
  container (or the Gremlin API); keep it separate from this per-agent lesson memory.
- **Session context** — persist compacted session summaries as their own `sector="context"` lessons if you
  want them recalled across sessions.
