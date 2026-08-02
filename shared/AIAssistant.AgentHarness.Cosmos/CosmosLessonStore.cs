using System.Net;
using AIAssistant.Harness;
using Microsoft.Azure.Cosmos;

namespace AIAssistant.Harness.Cosmos;

/// <summary>
/// Azure-native <see cref="ILessonStore"/>: one Cosmos DB container, <b>partition key /agent</b>. Each
/// agent's memory is its own logical partition, so scoped reads (this agent, this sector) stay cheap and
/// isolated while all agents share one container. Same three ops as the JSON store — moving JSON → Cosmos
/// is a store swap, not a rewrite: no agent code and no loop code changes.
///
/// The SDK serializer is set to camelCase so <c>Lesson.Id → "id"</c> (Cosmos's required key) and
/// <c>Lesson.Agent → "agent"</c> (the partition-key path) line up automatically.
/// </summary>
public sealed class CosmosLessonStore : ILessonStore
{
    private readonly Microsoft.Azure.Cosmos.CosmosClient _client;
    private readonly string _database;
    private readonly string _containerName;
    private readonly Lazy<Task<Container>> _container;

    public CosmosLessonStore(string connectionString, string database = "team6", string container = "lessons")
    {
        _client = new Microsoft.Azure.Cosmos.CosmosClient(connectionString, new CosmosClientOptions
        {
            SerializerOptions = new CosmosSerializationOptions { PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase },
        });
        _database = database;
        _containerName = container;
        _container = new Lazy<Task<Container>>(InitAsync);
    }

    private async Task<Container> InitAsync()
    {
        var db = await _client.CreateDatabaseIfNotExistsAsync(_database);
        var container = await db.Database.CreateContainerIfNotExistsAsync(new ContainerProperties(_containerName, "/agent"));
        return container.Container;
    }

    public async Task<IReadOnlyList<Lesson>> RetrieveAsync(string agent, AgentFeatures features, int topK, CancellationToken ct = default)
    {
        var container = await _container.Value;
        var query = new QueryDefinition(
                "SELECT * FROM c WHERE c.agent = @agent AND (c.sector = @sector OR c.sector = '*') ORDER BY c.hitRate DESC")
            .WithParameter("@agent", agent)
            .WithParameter("@sector", features.Sector);

        var results = new List<Lesson>();
        using var it = container.GetItemQueryIterator<Lesson>(query,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(agent) });
        while (it.HasMoreResults)
            results.AddRange(await it.ReadNextAsync(ct));

        // hitRate ordering comes from Cosmos; break ties by recency in memory (no composite index needed).
        return results.OrderByDescending(l => l.HitRate).ThenByDescending(l => l.Date).Take(topK).ToList();
    }

    public async Task WriteAsync(Lesson lesson, CancellationToken ct = default)
    {
        var container = await _container.Value;
        var toWrite = lesson;
        try
        {
            // Upsert but PRESERVE accumulated stats — refresh only the text, like the JSON store.
            var existing = (await container.ReadItemAsync<Lesson>(lesson.Id, new PartitionKey(lesson.Agent), cancellationToken: ct)).Resource;
            existing.Warning = lesson.Warning;
            existing.LearnedFrom = lesson.LearnedFrom;
            existing.Date = lesson.Date;
            toWrite = existing;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { /* new lesson */ }

        await container.UpsertItemAsync(toWrite, new PartitionKey(toWrite.Agent), cancellationToken: ct);
    }

    public async Task RecordApplicationAsync(string id, bool helped, CancellationToken ct = default, string? context = null)
    {
        var container = await _container.Value;
        var agent = id.Split('|', 2)[0]; // Id = "{agent}|{sector}|{trigger}" — the partition key is the agent
        try
        {
            var l = (await container.ReadItemAsync<Lesson>(id, new PartitionKey(agent), cancellationToken: ct)).Resource;
            l.TimesApplied++;
            if (helped) l.TimesHelped++;
            l.HitRate = l.TimesApplied == 0 ? 0 : Math.Round((double)l.TimesHelped / l.TimesApplied, 4);
            await container.UpsertItemAsync(l, new PartitionKey(agent), cancellationToken: ct);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { /* nothing to record */ }
    }

    public async Task<IReadOnlyList<Lesson>> AllAsync(CancellationToken ct = default)
    {
        var container = await _container.Value;
        var results = new List<Lesson>();
        using var it = container.GetItemQueryIterator<Lesson>(new QueryDefinition("SELECT * FROM c"));
        while (it.HasMoreResults)
            results.AddRange(await it.ReadNextAsync(ct));
        return results;
    }
}
