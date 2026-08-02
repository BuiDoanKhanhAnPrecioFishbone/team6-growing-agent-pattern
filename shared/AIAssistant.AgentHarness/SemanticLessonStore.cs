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
    private readonly int _cap;          // max lessons per agent before eviction (0 = unbounded)
    private readonly int _halfLife;     // retrieval decay half-life in days (0 = no decay)
    private readonly Func<DateTime> _now;

    public SemanticLessonStore(string path, IEmbedder? embedder = null, int shortlist = 12,
                               int cap = -1, int halfLifeDays = -1, Func<DateTime>? nowUtc = null)
    {
        _path = path;
        _embedder = embedder ?? Embeddings.FromEnvironment();
        _shortlist = shortlist;
        // Lifecycle knobs default OFF (env, else unbounded / no decay) so the store stays drop-in.
        _cap = cap >= 0 ? cap : EnvInt("AGENT_MEMORY_CAP", 0);
        _halfLife = halfLifeDays >= 0 ? halfLifeDays : EnvInt("AGENT_MEMORY_HALFLIFE_DAYS", 0);
        _now = nowUtc ?? (() => DateTime.UtcNow);
        _lessons = Load(path);
    }

    private static int EnvInt(string k, int d) => int.TryParse(Environment.GetEnvironmentVariable(k), out var v) ? v : d;

    // ── lifecycle helpers: a memory that only appends eventually rots — decay it, cap it, curate it. ──
    private double AgeDays(Lesson l)
    {
        var stamp = string.IsNullOrWhiteSpace(l.LastUsed) ? l.Date : l.LastUsed;
        return DateTime.TryParse(stamp, out var d) ? Math.Max(0, (_now() - d).TotalDays) : 0;
    }
    // Retrieval weight: recent + trusted lessons rank above stale/provisional ones (only when decay is on).
    private double RecencyWeight(Lesson l) => _halfLife <= 0 ? 1.0 : Math.Max(0.25, Math.Pow(0.5, AgeDays(l) / _halfLife));
    private static double TrustWeight(Lesson l) => l.Trust == Trust.Verified ? 1.0 : 0.85;
    // Eviction value: what a lesson is "worth" — trust + proven usefulness − staleness. Lowest goes first.
    private static double TrustBase(Trust t) => t == Trust.Verified ? 2.0 : t == Trust.Provisional ? 0.5 : -1.0;
    private double EvictValue(Lesson l) => TrustBase(l.Trust) + l.HitRate - (1.0 - RecencyWeight(l));

    private void EvictOverCap(string agent)
    {
        if (_cap <= 0) return;
        var forAgent = _lessons.Where(l => l.Agent == agent).ToList();
        if (forAgent.Count <= _cap) return;
        // drop the least valuable (junk/quarantined + stale provisional) until back at the cap
        foreach (var l in forAgent.OrderBy(EvictValue).Take(forAgent.Count - _cap).ToList())
            _lessons.Remove(l);
    }

    public async Task<IReadOnlyList<Lesson>> RetrieveAsync(string agent, AgentFeatures features, int topK, CancellationToken ct = default)
    {
        List<Lesson> candidates;
        lock (_lock)
        {
            // Step 1 — cheap metadata filter: this agent, this sector (or global), injectable (not Quarantined).
            candidates = _lessons
                .Where(l => l.Agent == agent && (l.Sector == features.Sector || l.Sector == "*") && l.Trust != Trust.Quarantined)
                .Select(Clone).ToList();
        }
        if (candidates.Count == 0) return Array.Empty<Lesson>();

        // No situation → v1 behaviour (hit-rate ordering).
        if (string.IsNullOrWhiteSpace(features.Situation))
            return candidates.OrderByDescending(l => l.HitRate).ThenByDescending(l => l.Date).Take(topK).ToList();

        // Step 2 — vector shortlist: cosine of the situation against each lesson's embedding, folding in
        // decay + trust when lifecycle is on (a stale or provisional lesson ranks below a fresh verified one).
        var q = await _embedder.EmbedAsync(features.Situation, ct);
        var shortlisted = candidates
            .Select(l => (l, score: (l.Embedding.Length > 0 ? Vec.Cosine(q, l.Embedding) : 0)
                                    * (_halfLife > 0 ? RecencyWeight(l) * TrustWeight(l) : 1.0)))
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

    private const double DedupTheta = 0.92;    // near-duplicate → merge instead of piling up
    private const double ConflictTheta = 0.60; // same-id guidance below this similarity ⇒ the rule changed (a conflict)
    private const int PromoteAfter = 2;        // provisional → verified after this many helpful applications
    private static readonly string[] InjectionMarkers =
    {
        "ignore previous", "ignore all previous", "disregard your", "disregard the above", "system:", "assistant:",
        "you are now", "new instructions", "forget the above", "reveal the system", "<script", "javascript:",
    };

    // A learned lesson is untrusted input that will be injected into a prompt — validate before it can be used.
    private static string? InjectionReason(Lesson l)
    {
        var text = $"{l.Condition} {l.Warning}".ToLowerInvariant();
        if (text.Length > 600) return "too long";
        foreach (var m in InjectionMarkers) if (text.Contains(m)) return $"injection marker: {m}";
        return null;
    }

    public async Task WriteAsync(Lesson lesson, CancellationToken ct = default)
    {
        // 1. injection-validation → Trust. Suspicious ⇒ Quarantined (stored, never injected). New learned ⇒ Provisional.
        lesson.Trust = InjectionReason(lesson) is not null ? Trust.Quarantined
                     : lesson.Trust == Trust.Verified ? Trust.Verified : Trust.Provisional;

        // 2. embed (condition + warning)
        if (lesson.Embedding.Length == 0)
        {
            var basis = string.IsNullOrWhiteSpace(lesson.Condition) ? lesson.Warning : $"{lesson.Condition} — {lesson.Warning}";
            lesson.Embedding = await _embedder.EmbedAsync(basis, ct);
        }

        // Optional semantic conflict flag (inert offline): if this new rule contradicts an existing VERIFIED
        // one, note it on provenance for human review — non-destructive, awaited outside the lock.
        if (Conflict.Enabled && lesson.Trust != Trust.Quarantined && lesson.Embedding.Length > 0)
        {
            List<(string Id, string Text, float[] Emb)> verified;
            lock (_lock)
                verified = _lessons
                    .Where(l => l.Agent == lesson.Agent && l.Id != lesson.Id && l.Trust == Trust.Verified && l.Embedding.Length > 0)
                    .Select(l => (l.Id, $"WHEN {l.Condition}: {l.Warning}", l.Embedding)).ToList();
            var band = verified
                .Where(n => { var c = Vec.Cosine(n.Emb, lesson.Embedding); return c >= ConflictTheta && c < DedupTheta; })
                .Select(n => (n.Id, n.Text)).ToList();
            var conflictId = await Conflict.ContradictsAsync($"WHEN {lesson.Condition}: {lesson.Warning}", band, ct);
            if (!string.IsNullOrEmpty(conflictId))
                lesson.LearnedFrom = $"{lesson.LearnedFrom} (possible conflict with {conflictId} — review)".Trim();
        }

        lock (_lock)
        {
            // exact upsert by id — re-learning refreshes text, keeps stats & trust…
            var existing = _lessons.FirstOrDefault(l => l.Id == lesson.Id);
            if (existing is not null)
            {
                // …unless the guidance for the SAME trigger materially changed (a conflict / rule flip):
                // don't let the new rule inherit Verified trust — demote to Provisional and reset stats so
                // it must re-earn its place. This is how the memory self-corrects instead of trusting stale.
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
                Save(); return;
            }
            // 3. dedup/merge — a near-duplicate of an existing (non-quarantined) lesson refreshes it instead of piling up
            if (lesson.Trust != Trust.Quarantined)
            {
                var dup = _lessons.FirstOrDefault(l => l.Agent == lesson.Agent && l.Trust != Trust.Quarantined
                            && l.Embedding.Length > 0 && Vec.Cosine(l.Embedding, lesson.Embedding) >= DedupTheta);
                if (dup is not null)
                {
                    dup.Warning = lesson.Warning; dup.Condition = lesson.Condition; dup.LastUsed = lesson.Date;
                    Save(); return; // merged
                }
            }
            // 4. new lesson — add, then evict if this agent is now over its memory cap (bounded memory)
            _lessons.Add(lesson);
            EvictOverCap(lesson.Agent);
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
                // promotion: a provisional lesson that keeps helping earns Verified.
                if (l.Trust == Trust.Provisional && l.TimesHelped >= PromoteAfter && l.HitRate >= 0.6) l.Trust = Trust.Verified;
                Save();
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>Human confirmation at a gate promotes a provisional lesson to Verified.</summary>
    public Task PromoteAsync(string id, CancellationToken ct = default)
    {
        lock (_lock) { var l = _lessons.FirstOrDefault(x => x.Id == id); if (l is { Trust: Trust.Provisional }) { l.Trust = Trust.Verified; Save(); } }
        return Task.CompletedTask;
    }

    /// <summary>Promote all of an agent's provisional lessons — a human confirming that agent's gate endorses them.</summary>
    public Task PromoteForAgentAsync(string agent, CancellationToken ct = default)
    {
        lock (_lock) { foreach (var l in _lessons.Where(l => l.Agent == agent && l.Trust == Trust.Provisional)) l.Trust = Trust.Verified; Save(); }
        return Task.CompletedTask;
    }

    private const double ConsolidateTheta = 0.80; // related-but-distinct: above this clusters, below DedupTheta stays separate

    /// <summary>
    /// Consolidate an agent's memory: greedily cluster RELATED lessons (cosine ≥ 0.80, below the dedup line) and
    /// replace each cluster of ≥ <paramref name="minCluster"/> with ONE distilled meta-lesson — so memory
    /// summarizes itself at scale instead of growing linearly. Preserves aggregate stats and the strongest
    /// trust of its members. A maintenance op (call periodically), not on the write path. Returns clusters folded.
    /// </summary>
    public async Task<int> ConsolidateAsync(string agent, int minCluster = 3, CancellationToken ct = default)
    {
        // Snapshot the injectable, embedded lessons for this agent (skip quarantined/junk).
        List<Lesson> pool;
        lock (_lock)
            pool = _lessons.Where(l => l.Agent == agent && l.Trust != Trust.Quarantined && l.Embedding.Length > 0)
                           .Select(Clone).ToList();

        // Greedy single-pass clustering by embedding similarity (deterministic given store order).
        var used = new HashSet<string>();
        var clusters = new List<List<Lesson>>();
        foreach (var seed in pool)
        {
            if (used.Contains(seed.Id)) continue;
            var cluster = new List<Lesson> { seed }; used.Add(seed.Id);
            foreach (var other in pool)
            {
                if (used.Contains(other.Id)) continue;
                var c = Vec.Cosine(seed.Embedding, other.Embedding);
                if (c >= ConsolidateTheta && c < DedupTheta) { cluster.Add(other); used.Add(other.Id); }
            }
            if (cluster.Count >= minCluster) clusters.Add(cluster);
        }
        if (clusters.Count == 0) return 0;

        // Build each meta-lesson OUTSIDE the lock (summarizer may call a model).
        var metas = new List<Lesson>();
        foreach (var cluster in clusters)
        {
            var warning = await Consolidation.SummarizeAsync(cluster.Select(l => l.Warning).ToList(), ct);
            if (string.IsNullOrWhiteSpace(warning)) continue;
            var sector = cluster[0].Sector;
            var applied = cluster.Sum(l => l.TimesApplied);
            var helped = cluster.Sum(l => l.TimesHelped);
            var meta = new Lesson
            {
                Id = $"{agent}|{sector}|meta:{StableHash(string.Concat(cluster.Select(l => l.Id).OrderBy(x => x)))}",
                Agent = agent, Sector = sector, Trigger = "consolidated",
                Condition = cluster.OrderBy(l => l.Condition.Length).First().Condition, // the most general condition
                Warning = warning, Type = LessonType.Strategy,
                // the distillation is at least as trustworthy as its strongest source
                Trust = cluster.Any(l => l.Trust == Trust.Verified) ? Trust.Verified : Trust.Provisional,
                TimesApplied = applied, TimesHelped = helped,
                HitRate = applied == 0 ? 0 : Math.Round((double)helped / applied, 4),
                LearnedFrom = $"consolidated {cluster.Count} lessons: {string.Join(", ", cluster.Select(l => l.Trigger))}",
                Date = cluster.Max(l => l.Date) ?? "", LastUsed = cluster.Max(l => l.Date) ?? "",
            };
            meta.Embedding = await _embedder.EmbedAsync($"{meta.Condition} — {meta.Warning}", ct);
            metas.Add(meta);
        }

        lock (_lock)
        {
            foreach (var cluster in clusters)
                foreach (var m in cluster)
                    _lessons.RemoveAll(l => l.Id == m.Id);
            _lessons.AddRange(metas);
            Save();
        }
        return clusters.Count;
    }

    private static string StableHash(string s)
    { unchecked { uint h = 2166136261; foreach (var c in s) { h ^= c; h *= 16777619; } return h.ToString("x8"); } }

    /// <summary>Remove one lesson by id — used by graduation to evict a lesson the weights have absorbed.</summary>
    public Task RemoveAsync(string id, CancellationToken ct = default)
    { lock (_lock) { if (_lessons.RemoveAll(l => l.Id == id) > 0) Save(); } return Task.CompletedTask; }

    /// <summary>Wipe the memory — the UI's reset, so a fresh run learns from scratch.</summary>
    public void Clear() { lock (_lock) { _lessons.Clear(); Save(); } }

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
