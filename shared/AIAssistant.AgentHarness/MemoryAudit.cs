namespace AIAssistant.Harness;

// ─────────────────────────────────────────────────────────────────────────────
// MEMORY AUDIT — defense-in-depth for the learning channel. Write-time gating is the
// first wall (injection-validation → Quarantine); this is the periodic second wall the
// security literature calls for (post-hoc auditing, e.g. MemAudit) on top of prevention.
// A scheduled sweep re-screens the whole store and cleans it:
//   • re-quarantine any active lesson that now trips injection validation (defense-in-depth),
//   • evict "dead" provisional lessons (tried repeatedly, never helped) that clutter recall,
//   • prune old superseded tombstones beyond a keep-window (bounded audit history).
// Deterministic and offline; returns a report so the sweep is observable, not silent.
// ─────────────────────────────────────────────────────────────────────────────
public static class MemoryAudit
{
    public sealed record Report(int Scanned, int Requarantined, int DeadEvicted, int TombstonesPruned, IReadOnlyList<string> Notes)
    {
        public bool Clean => Requarantined == 0 && DeadEvicted == 0 && TombstonesPruned == 0;
    }

    /// <summary>Audit one agent's memory. <paramref name="deadAfter"/> = applications with zero help before a
    /// provisional lesson is judged dead; <paramref name="keepTombstones"/> = how many superseded versions to retain.</summary>
    public static async Task<Report> RunAsync(
        SemanticLessonStore store, string agent, int deadAfter = 4, int keepTombstones = 3, CancellationToken ct = default)
    {
        var all = (await store.AllAsync(ct)).Where(l => l.Agent == agent).ToList();
        int req = 0, dead = 0, pruned = 0;
        var notes = new List<string>();

        foreach (var l in all)
        {
            if (!string.IsNullOrEmpty(l.ValidTo)) continue; // tombstones handled below

            // 1. re-screen for injection that slipped in (e.g. a lesson edited/imported around the write path).
            if (l.Trust != Trust.Quarantined && SemanticLessonStore.LooksInjected(l))
            {
                await store.SetTrustAsync(l.Id, Trust.Quarantined, ct);
                req++; notes.Add($"re-quarantined {Short(l)}");
            }
            // 2. evict a dead provisional: applied enough to judge, never once helped, never corroborated.
            else if (l.Trust == Trust.Provisional && l.TimesApplied >= deadAfter && l.TimesHelped == 0 && l.HelpedContexts.Count == 0)
            {
                await store.RemoveAsync(l.Id, ct);
                dead++; notes.Add($"evicted dead provisional {Short(l)}");
            }
        }

        // 3. prune old superseded tombstones per original lesson, keeping the most recent few for history.
        foreach (var grp in all.Where(l => !string.IsNullOrEmpty(l.ValidTo)).GroupBy(l => l.SupersededBy))
        {
            foreach (var old in grp.OrderByDescending(l => l.ValidTo).Skip(keepTombstones))
            {
                await store.RemoveAsync(old.Id, ct);
                pruned++;
            }
        }

        return new Report(all.Count, req, dead, pruned, notes);
    }

    private static string Short(Lesson l) => $"[{l.Trigger}] {(l.Warning.Length <= 48 ? l.Warning : l.Warning[..48] + "…")}";
}
