using System.Text.Json;

namespace AIAssistant.Harness;

/// <summary>
/// The demo / offline backing for <see cref="ILessonStore"/>: one JSON file per agent, held in memory
/// and saved on every write. Zero infra, inspectable by hand, and it makes the loop's learning visible
/// via GET /lessons. Swap for CosmosLessonStore in the cloud — the loop and the agent don't change.
/// </summary>
public sealed class JsonLessonStore : ILessonStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private readonly List<Lesson> _lessons;

    public JsonLessonStore(string path)
    {
        _path = path;
        _lessons = Load(path);
    }

    public Task<IReadOnlyList<Lesson>> RetrieveAsync(string agent, AgentFeatures features, int topK, CancellationToken ct = default)
    {
        lock (_lock)
        {
            IReadOnlyList<Lesson> result = _lessons
                .Where(l => l.Agent == agent && (l.Sector == features.Sector || l.Sector == "*"))
                .OrderByDescending(l => l.HitRate)
                .ThenByDescending(l => l.Date)
                .Take(topK)
                .Select(Clone)
                .ToList();
            return Task.FromResult(result);
        }
    }

    public Task WriteAsync(Lesson lesson, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var existing = _lessons.FirstOrDefault(l => l.Id == lesson.Id);
            if (existing is null)
            {
                _lessons.Add(lesson);
            }
            else
            {
                existing.Warning = lesson.Warning;
                existing.LearnedFrom = lesson.LearnedFrom;
                existing.Date = lesson.Date;
            }
            Save();
        }
        return Task.CompletedTask;
    }

    public Task RecordApplicationAsync(string id, bool helped, CancellationToken ct = default, string? context = null)
    {
        lock (_lock)
        {
            var l = _lessons.FirstOrDefault(x => x.Id == id);
            if (l is not null)
            {
                l.TimesApplied++;
                if (helped) l.TimesHelped++;
                l.HitRate = l.TimesApplied == 0 ? 0 : Math.Round((double)l.TimesHelped / l.TimesApplied, 4);
                Save();
            }
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Lesson>> AllAsync(CancellationToken ct = default)
    {
        lock (_lock) { return Task.FromResult((IReadOnlyList<Lesson>)_lessons.Select(Clone).ToList()); }
    }

    /// <summary>Wipe the memory — used by the UI's "reset" so a fresh run shows agents learning from scratch.</summary>
    public void Clear()
    {
        lock (_lock) { _lessons.Clear(); Save(); }
    }

    private void Save() => File.WriteAllText(_path, JsonSerializer.Serialize(_lessons, JsonOpts));

    private static List<Lesson> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new List<Lesson>();
            return JsonSerializer.Deserialize<List<Lesson>>(File.ReadAllText(path), JsonOpts) ?? new List<Lesson>();
        }
        catch { return new List<Lesson>(); }
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static Lesson Clone(Lesson l) => new()
    {
        Id = l.Id, Agent = l.Agent, Sector = l.Sector, Trigger = l.Trigger, Warning = l.Warning,
        LearnedFrom = l.LearnedFrom, Date = l.Date,
        TimesApplied = l.TimesApplied, TimesHelped = l.TimesHelped, HitRate = l.HitRate,
        Type = l.Type, Condition = l.Condition, Embedding = l.Embedding, Trust = l.Trust, LastUsed = l.LastUsed,
        Importance = l.Importance, HelpedContexts = new(l.HelpedContexts), ValidTo = l.ValidTo, SupersededBy = l.SupersededBy,
    };
}
