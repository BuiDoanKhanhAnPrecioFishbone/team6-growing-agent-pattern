using System.Text.Json;

namespace AIAssistant.Harness;

/// <summary>
/// Memory v2 backing for <see cref="ILessonStore"/> — same seam, smarter inside. D1-2 skeleton:
/// metadata filter → embed the situation → cosine shortlist. The LLM-recall side-query (D3-4) and the
/// write-time refine/injection-validation (D5) slot into the marked hooks without touching the harness.
/// Falls back to hit-rate ordering when no situation is supplied, so it is drop-in for the v1 flow.
/// </summary>
public sealed class SemanticLessonStore : ILessonStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private readonly List<Lesson> _lessons;
    private readonly IEmbedder _embedder;
    private readonly int _shortlist;

    public SemanticLessonStore(string path, IEmbedder? embedder = null, int shortlist = 12)
    {
        _path = path;
        _embedder = embedder ?? Embeddings.FromEnvironment();
        _shortlist = shortlist;
        _lessons = Load(path);
    }

    public async Task<IReadOnlyList<Lesson>> RetrieveAsync(string agent, AgentFeatures features, int topK, CancellationToken ct = default)
    {
        List<Lesson> candidates;
        lock (_lock)
        {
            // Step 1 — cheap metadata filter: this agent, this sector (or global), injectable (Verified), not stale.
            candidates = _lessons
                .Where(l => l.Agent == agent && (l.Sector == features.Sector || l.Sector == "*") && l.Trust == Trust.Verified)
                .Select(Clone).ToList();
        }
        if (candidates.Count == 0) return Array.Empty<Lesson>();

        // No situation → v1 behaviour (hit-rate ordering).
        if (string.IsNullOrWhiteSpace(features.Situation))
            return candidates.OrderByDescending(l => l.HitRate).ThenByDescending(l => l.Date).Take(topK).ToList();

        // Step 2 — vector shortlist: cosine of the situation against each lesson's embedding.
        var q = await _embedder.EmbedAsync(features.Situation, ct);
        var shortlisted = candidates
            .Select(l => (l, score: l.Embedding.Length > 0 ? Vec.Cosine(q, l.Embedding) : 0))
            .OrderByDescending(x => x.score).ThenByDescending(x => x.l.HitRate)
            .Take(Math.Max(topK, _shortlist))
            .Select(x => x.l)
            .ToList();

        // Step 3 — LLM recall: send only (id, condition, one-line summary) — NOT the full text — and ask a
        // cheap model which of the shortlist genuinely APPLY. Null ⇒ recall unavailable ⇒ keep vector order.
        var cands = shortlisted
            .Select(l => (l.Id, Cond: string.IsNullOrWhiteSpace(l.Condition) ? "(general)" : l.Condition, Summary: OneLine(l.Warning)))
            .ToList();
        var appliedIds = await Recall.ApplicableAsync(features.Situation, cands, topK, ct);
        if (appliedIds is null)
            return shortlisted.Take(topK).ToList();

        // Step 4 — two-phase load: materialize the FULL lessons only for the picked ids, in the model's order.
        // (In a Cosmos/graph backing, step 3 is a projection query and this is a point-read per id.)
        var byId = shortlisted.ToDictionary(l => l.Id);
        var picked = appliedIds.Where(byId.ContainsKey).Select(id => byId[id]).Take(topK).ToList();
        return picked.Count > 0 ? picked : shortlisted.Take(topK).ToList();
    }

    public async Task WriteAsync(Lesson lesson, CancellationToken ct = default)
    {
        // D5 hooks: validate (injection) → set Trust=Provisional → dedup/merge → conflict-check.
        if (lesson.Embedding.Length == 0)
        {
            var basis = string.IsNullOrWhiteSpace(lesson.Condition) ? lesson.Warning : $"{lesson.Condition} — {lesson.Warning}";
            lesson.Embedding = await _embedder.EmbedAsync(basis, ct);
        }
        lock (_lock)
        {
            var existing = _lessons.FirstOrDefault(l => l.Id == lesson.Id);
            if (existing is null) _lessons.Add(lesson);
            else
            {
                existing.Warning = lesson.Warning; existing.Condition = lesson.Condition; existing.Type = lesson.Type;
                existing.Embedding = lesson.Embedding; existing.LearnedFrom = lesson.LearnedFrom; existing.Date = lesson.Date;
            }
            Save();
        }
    }

    public Task RecordApplicationAsync(string id, bool helped, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var l = _lessons.FirstOrDefault(x => x.Id == id);
            if (l is not null)
            {
                l.TimesApplied++;
                if (helped) l.TimesHelped++;
                l.HitRate = l.TimesApplied == 0 ? 0 : Math.Round((double)l.TimesHelped / l.TimesApplied, 4);
                l.LastUsed = l.Date;
                Save();
            }
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Lesson>> AllAsync(CancellationToken ct = default)
    {
        lock (_lock) { return Task.FromResult((IReadOnlyList<Lesson>)_lessons.Select(Clone).ToList()); }
    }

    private static string OneLine(string w) => string.IsNullOrEmpty(w) ? "" : (w.Length <= 90 ? w : w[..90] + "…");

    private void Save() => File.WriteAllText(_path, JsonSerializer.Serialize(_lessons, JsonOpts));

    private static List<Lesson> Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<List<Lesson>>(File.ReadAllText(path), JsonOpts) ?? new() : new(); }
        catch { return new(); }
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static Lesson Clone(Lesson l) => new()
    {
        Id = l.Id, Agent = l.Agent, Sector = l.Sector, Trigger = l.Trigger, Warning = l.Warning,
        LearnedFrom = l.LearnedFrom, Date = l.Date, TimesApplied = l.TimesApplied, TimesHelped = l.TimesHelped, HitRate = l.HitRate,
        Type = l.Type, Condition = l.Condition, Embedding = l.Embedding, Trust = l.Trust, LastUsed = l.LastUsed,
    };
}
