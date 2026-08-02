namespace AIAssistant.Harness;

// ─────────────────────────────────────────────────────────────────────────────
// InMemoryLessonStore — the smallest possible ILessonStore, to PROVE the seam. The
// harness talks to memory through one interface; the backing is yours. Json (a file),
// Semantic (embeddings), Cosmos (server-side vector search) and this (RAM) all satisfy
// the same four methods — so "bring your own vector store" (pgvector, Qdrant, Azure AI
// Search, …) is a ~40-line adapter, not a rewrite. Handy for tests and quick demos.
// ─────────────────────────────────────────────────────────────────────────────
public sealed class InMemoryLessonStore : ILessonStore
{
    private readonly List<Lesson> _lessons = new();
    private readonly object _lock = new();

    public Task<IReadOnlyList<Lesson>> RetrieveAsync(string agent, AgentFeatures features, int topK, CancellationToken ct = default)
    {
        lock (_lock)
        {
            IReadOnlyList<Lesson> r = _lessons
                .Where(l => l.Agent == agent && (l.Sector == features.Sector || l.Sector == "*")
                            && l.Trust != Trust.Quarantined && string.IsNullOrEmpty(l.ValidTo)
                            && Scope.Matches(l.Owner, features))
                .OrderByDescending(l => Scope.Rank(l.Owner)).ThenByDescending(l => l.HitRate)
                .ThenByDescending(l => l.Importance).ThenByDescending(l => l.Date)
                .Take(topK).ToList();
            return Task.FromResult(r);
        }
    }

    public Task WriteAsync(Lesson lesson, CancellationToken ct = default)
    {
        // untrusted text is injected into a prompt → screen it, exactly like the persistent stores.
        if (SemanticLessonStore.LooksInjected(lesson)) lesson.Trust = Trust.Quarantined;
        lock (_lock)
        {
            var existing = _lessons.FirstOrDefault(l => l.Id == lesson.Id);
            if (existing is null) _lessons.Add(lesson);
            else { existing.Warning = lesson.Warning; existing.Condition = lesson.Condition; existing.Date = lesson.Date; }
        }
        return Task.CompletedTask;
    }

    public Task RecordApplicationAsync(string id, bool helped, CancellationToken ct = default, string? context = null)
    {
        lock (_lock)
        {
            var l = _lessons.FirstOrDefault(x => x.Id == id);
            if (l is null) return Task.CompletedTask;
            l.TimesApplied++;
            if (helped) l.TimesHelped++;
            l.HitRate = l.TimesApplied == 0 ? 0 : Math.Round((double)l.TimesHelped / l.TimesApplied, 4);
            if (helped && !string.IsNullOrWhiteSpace(context) && !l.HelpedContexts.Contains(context!) && l.HelpedContexts.Count < 8)
                l.HelpedContexts.Add(context!);
            var support = l.HelpedContexts.Count > 0 ? l.HelpedContexts.Count : l.TimesHelped;
            if (l.Trust == Trust.Provisional && support >= 2 && l.HitRate >= 0.6) l.Trust = Trust.Verified;
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Lesson>> AllAsync(CancellationToken ct = default)
    { lock (_lock) { return Task.FromResult((IReadOnlyList<Lesson>)_lessons.ToList()); } }
}
