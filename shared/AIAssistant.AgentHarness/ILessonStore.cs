namespace AIAssistant.Harness;

/// <summary>
/// The agent's episodic memory — the three graph operations from the pattern, behind one interface so
/// the backing store is swappable without touching the loop or the agent:
///   • <see cref="JsonLessonStore"/>  — one file per agent (demo / offline).
///   • CosmosLessonStore              — Cosmos DB, partition key /agent (the platform; separate project).
/// Async because a cloud store is async; the JSON store simply wraps its in-memory ops in completed tasks.
/// </summary>
public interface ILessonStore
{
    /// <summary>Scoped retrieval: this agent's lessons for this sector (or the "*" global bucket),
    /// most-trusted first, bounded to <paramref name="topK"/> so the prompt is never flooded.</summary>
    Task<IReadOnlyList<Lesson>> RetrieveAsync(string agent, AgentFeatures features, int topK, CancellationToken ct = default);

    /// <summary>Upsert by Id. A re-learned lesson refreshes its text but KEEPS its accumulated stats.</summary>
    Task WriteAsync(Lesson lesson, CancellationToken ct = default);

    /// <summary>hitRate = timesHelped / timesApplied. A lesson that stops helping decays out of retrieval.
    /// <paramref name="context"/> (the situation it helped in) is recorded so promotion can require
    /// corroboration across DISTINCT situations — a poisoned lesson can't self-promote by repeating one case.</summary>
    Task RecordApplicationAsync(string id, bool helped, CancellationToken ct = default, string? context = null);

    /// <summary>Everything the store holds — for the GET /lessons inspector.</summary>
    Task<IReadOnlyList<Lesson>> AllAsync(CancellationToken ct = default);
}
