using System.Collections.ObjectModel;
using System.Net;
using AIAssistant.Harness;
using Microsoft.Azure.Cosmos;

namespace AIAssistant.Harness.Cosmos;

/// <summary>
/// Azure-native <b>semantic</b> memory: the same <see cref="ILessonStore"/> seam as everything else, but the
/// lessons live in Cosmos DB with their embedding stored on the document and retrieval done by
/// <b>server-side vector search</b> (<c>VectorDistance</c> over a DiskANN vector index). This is the backing
/// teammates copy to persist their own agent's lessons/context/memory in the cloud with semantic recall.
///
/// Partition key <c>/agent</c> (each agent's memory is its own partition). The container is created with a
/// vector-embedding policy on <c>/embedding</c>; the account must have the NoSQL vector-search capability
/// enabled (see docs/COSMOS-MEMORY.md). Embeddings come from <see cref="IEmbedder"/> — use the Foundry
/// text-embedding-3-small embedder (1536 dims) so vectors match the container policy.
/// </summary>
public sealed class CosmosSemanticLessonStore : ILessonStore
{
    private readonly CosmosClient _client;
    private readonly string _db, _containerName;
    private readonly IEmbedder _embedder;
    private readonly int _dimensions;
    private readonly Lazy<Task<Container>> _container;

    private const double DedupTheta = 0.92;    // cosine ≥ this ⇒ near-duplicate, merge instead of adding
    private const double ConflictTheta = 0.60; // same-id guidance below this ⇒ the rule changed (demote trust)

    public CosmosSemanticLessonStore(string connectionString, string database = "team6", string container = "lessons",
                                     IEmbedder? embedder = null, int dimensions = 1536)
    {
        _client = new CosmosClient(connectionString, new CosmosClientOptions
        {
            SerializerOptions = new CosmosSerializationOptions { PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase },
        });
        _db = database;
        _containerName = container;
        _embedder = embedder ?? Embeddings.FromEnvironment();
        _dimensions = dimensions;
        _container = new Lazy<Task<Container>>(InitAsync);
    }

    // Self-provisioning: create the container WITH the vector policy + index (both are set at creation time
    // and cannot be added later, so a fresh container is required for vector search).
    private async Task<Container> InitAsync()
    {
        var db = (await _client.CreateDatabaseIfNotExistsAsync(_db)).Database;

        var embeddings = new Collection<Embedding>
        {
            new Embedding
            {
                Path = "/embedding",
                DataType = VectorDataType.Float32,
                DistanceFunction = DistanceFunction.Cosine,
                Dimensions = _dimensions,
            },
        };
        var props = new ContainerProperties(_containerName, "/agent")
        {
            VectorEmbeddingPolicy = new VectorEmbeddingPolicy(embeddings),
        };
        // Keep the big vector out of the normal (range) index; put a DiskANN vector index on it instead.
        props.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/embedding/*" });
        props.IndexingPolicy.VectorIndexes.Add(new VectorIndexPath { Path = "/embedding", Type = VectorIndexType.DiskANN });

        return (await db.CreateContainerIfNotExistsAsync(props)).Container;
    }

    public async Task<IReadOnlyList<Lesson>> RetrieveAsync(string agent, AgentFeatures features, int topK, CancellationToken ct = default)
    {
        var container = await _container.Value;
        var quarantined = (int)Trust.Quarantined;

        QueryDefinition query;
        if (string.IsNullOrWhiteSpace(features.Situation))
        {
            // No situation → v1 behaviour: hit-rate ordering (drop-in with the JSON/exact stores).
            query = new QueryDefinition(
                    "SELECT * FROM c WHERE c.agent=@a AND (c.sector=@s OR c.sector='*') AND c.trust != @q ORDER BY c.hitRate DESC")
                .WithParameter("@a", agent).WithParameter("@s", features.Sector).WithParameter("@q", quarantined);
        }
        else
        {
            // Situation → embed it and let Cosmos rank by vector distance (nearest first).
            var vec = await _embedder.EmbedAsync(features.Situation, ct);
            query = new QueryDefinition(
                    "SELECT TOP @k * FROM c WHERE c.agent=@a AND (c.sector=@s OR c.sector='*') AND c.trust != @q " +
                    "ORDER BY VectorDistance(c.embedding, @vec)")
                .WithParameter("@k", topK).WithParameter("@a", agent).WithParameter("@s", features.Sector)
                .WithParameter("@q", quarantined).WithParameter("@vec", vec);
        }

        var results = new List<Lesson>();
        using var it = container.GetItemQueryIterator<Lesson>(query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(agent) });
        while (it.HasMoreResults) results.AddRange(await it.ReadNextAsync(ct));
        return results.Take(topK).ToList();
    }

    public async Task WriteAsync(Lesson lesson, CancellationToken ct = default)
    {
        var container = await _container.Value;

        // 1. injection-validation → Trust (suspicious ⇒ Quarantined, stored but never retrieved).
        lesson.Trust = InjectionReason(lesson) is not null ? Trust.Quarantined
                     : lesson.Trust == Trust.Verified ? Trust.Verified : Trust.Provisional;

        // 2. embed (condition + warning) if not already embedded.
        if (lesson.Embedding.Length == 0)
        {
            var basis = string.IsNullOrWhiteSpace(lesson.Condition) ? lesson.Warning : $"{lesson.Condition} — {lesson.Warning}";
            lesson.Embedding = await _embedder.EmbedAsync(basis, ct);
        }

        // 3. same-id upsert — preserve stats; if the guidance materially changed (a conflict), demote trust.
        try
        {
            var existing = (await container.ReadItemAsync<Lesson>(lesson.Id, new PartitionKey(lesson.Agent), cancellationToken: ct)).Resource;
            var diverged = existing.Embedding.Length > 0 && lesson.Embedding.Length > 0
                           && Vec.Cosine(existing.Embedding, lesson.Embedding) < ConflictTheta;
            existing.Warning = lesson.Warning; existing.Condition = lesson.Condition; existing.Type = lesson.Type;
            existing.Embedding = lesson.Embedding; existing.Date = lesson.Date;
            if (diverged && existing.Trust != Trust.Quarantined)
            {
                existing.Trust = Trust.Provisional; existing.TimesApplied = 0; existing.TimesHelped = 0; existing.HitRate = 0;
                existing.LearnedFrom = lesson.LearnedFrom + " (superseded a conflicting prior rule)";
            }
            else existing.LearnedFrom = lesson.LearnedFrom;
            await container.UpsertItemAsync(existing, new PartitionKey(existing.Agent), cancellationToken: ct);
            return;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { /* new lesson */ }

        await container.UpsertItemAsync(lesson, new PartitionKey(lesson.Agent), cancellationToken: ct);
    }

    public async Task RecordApplicationAsync(string id, bool helped, CancellationToken ct = default)
    {
        var container = await _container.Value;
        var agent = id.Split('|', 2)[0]; // Id = "{agent}|{sector}|{trigger}"
        try
        {
            var l = (await container.ReadItemAsync<Lesson>(id, new PartitionKey(agent), cancellationToken: ct)).Resource;
            l.TimesApplied++;
            if (helped) l.TimesHelped++;
            l.HitRate = l.TimesApplied == 0 ? 0 : Math.Round((double)l.TimesHelped / l.TimesApplied, 4);
            if (l.Trust == Trust.Provisional && l.TimesHelped >= 2 && l.HitRate >= 0.6) l.Trust = Trust.Verified;
            await container.UpsertItemAsync(l, new PartitionKey(agent), cancellationToken: ct);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { /* nothing to record */ }
    }

    /// <summary>Human confirmation at a gate promotes an agent's provisional lessons to Verified.</summary>
    public async Task PromoteForAgentAsync(string agent, CancellationToken ct = default)
    {
        var container = await _container.Value;
        var q = new QueryDefinition("SELECT * FROM c WHERE c.agent=@a AND c.trust=@p")
            .WithParameter("@a", agent).WithParameter("@p", (int)Trust.Provisional);
        using var it = container.GetItemQueryIterator<Lesson>(q, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(agent) });
        while (it.HasMoreResults)
            foreach (var l in await it.ReadNextAsync(ct))
            {
                l.Trust = Trust.Verified;
                await container.UpsertItemAsync(l, new PartitionKey(agent), cancellationToken: ct);
            }
    }

    public async Task<IReadOnlyList<Lesson>> AllAsync(CancellationToken ct = default)
    {
        var container = await _container.Value;
        var results = new List<Lesson>();
        using var it = container.GetItemQueryIterator<Lesson>(new QueryDefinition("SELECT * FROM c"));
        while (it.HasMoreResults) results.AddRange(await it.ReadNextAsync(ct));
        return results;
    }

    // A learned lesson is untrusted text that gets injected into a prompt — screen it before it can be used.
    private static readonly string[] InjectionMarkers =
    {
        "ignore previous", "ignore all previous", "disregard your", "disregard the above", "system:", "assistant:",
        "you are now", "new instructions", "forget the above", "reveal the system", "<script", "javascript:",
    };
    private static string? InjectionReason(Lesson l)
    {
        var text = $"{l.Condition} {l.Warning}".ToLowerInvariant();
        if (text.Length > 600) return "too long";
        foreach (var m in InjectionMarkers) if (text.Contains(m)) return $"injection marker: {m}";
        return null;
    }
}
