using System.Text.Json;

namespace AIAssistant.Harness;

// ─────────────────────────────────────────────────────────────────────────────
// THE SLOW LOOP — moving knowledge from CONTEXT into WEIGHTS. The fast loop grows the
// agent in-context (lessons). Eventually a lesson is stable and recurring enough that
// it should live in the model, not the prompt. This is the graduation path the field
// endorses, cheapest-first:
//
//   1. ReST-EM / rejection-sampling SFT — filter the flywheel's PASSING trajectories,
//      keep the best completion per task, fine-tune FROM BASE (+ a general-data mix to
//      resist forgetting). Simplest, most stable slow loop; no RL infra. (STaR/ReST-EM,
//      FireAct.) Preference(DPO) and RL(GRPO/ART) are the next rungs off the same log.
//   2. Graduation + pruning — after a bake, a lesson whose knowledge is now IN THE
//      WEIGHTS is redundant in context. Re-test it WITHOUT injection on the baked model;
//      if it still passes, evict it — GATED on the eval, because a lossy bake means keep
//      the lesson as a safety net. Memory shrinks while quality holds; context cost falls.
//
// Verified-gated: only reward-passing trajectories are ever exported — the documented
// antidote to model-collapse / data-autophagy (never train on unfiltered self-output).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>One rejection-sampled SFT target: the best passing completion for a task, with its system prompt.</summary>
public sealed record SftSample(string System, string Task, string Completion, double Score);

/// <summary>Rejection-sampling / ReST-EM dataset construction from harness runs — the cheapest slow loop.</summary>
public static class RestEm
{
    private static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The E-step (filter): across many runs, keep only attempts that PASSED at/above <paramref name="threshold"/>,
    /// and for each task keep the single best-scoring one (rejection sampling). Verified-gated by construction —
    /// failing self-output never enters the set.
    /// </summary>
    public static IReadOnlyList<SftSample> Select(
        IEnumerable<(string system, string task, HarnessOutcome outcome)> runs, double threshold = 1.0)
    {
        var best = new Dictionary<string, SftSample>();
        foreach (var (system, task, o) in runs)
        {
            foreach (var a in o.Attempts ?? Array.Empty<Attempt>())
            {
                if (!a.Pass || a.Score < threshold || string.IsNullOrWhiteSpace(a.Draft)) continue;
                if (!best.TryGetValue(task, out var cur) || a.Score > cur.Score)
                    best[task] = new SftSample(system, task, a.Draft, a.Score);
            }
            // fall back to the run's Best if no per-attempt record survived (Attempts optional)
            if (!best.ContainsKey(task) && o.Best.Pass && o.Best.Score >= threshold && !string.IsNullOrEmpty(o.BestDraft))
                best[task] = new SftSample(system, task, o.BestDraft!, o.Best.Score);
        }
        return best.Values.ToList();
    }

    /// <summary>Chat fine-tune JSONL (Azure Foundry / OpenAI format): one {messages:[system?,user,assistant]} per line.
    /// <paramref name="generalMix"/> lets you fold in held-out general examples — the anti-forgetting lever.</summary>
    public static string ToChatJsonl(IEnumerable<SftSample> samples, IEnumerable<SftSample>? generalMix = null)
    {
        var all = samples.Concat(generalMix ?? Enumerable.Empty<SftSample>());
        return string.Join("\n", all.Select(s =>
        {
            var msgs = new List<object>();
            if (!string.IsNullOrWhiteSpace(s.System)) msgs.Add(new { role = "system", content = s.System });
            msgs.Add(new { role = "user", content = s.Task });
            msgs.Add(new { role = "assistant", content = s.Completion });
            return JsonSerializer.Serialize(new { messages = msgs }, Opts);
        }));
    }

    /// <summary>The exact CLI to submit the dataset as an Azure AI Foundry fine-tune, for the docs/demo.</summary>
    public static string FoundrySubmitHint(string datasetPath, string baseModel) =>
        $"az ml job create --file finetune.yml   # base={baseModel}, training_data={datasetPath}\n" +
        "  # ReST-EM rule: fine-tune FROM BASE each round on the accumulated passing set; keep a general-data mix.";
}

/// <summary>
/// Graduation: prune lessons the weights have absorbed. For each Verified lesson, score its task on the BAKED
/// model WITHOUT injecting the lesson; if it clears the bar, the knowledge is now in the weights → evict.
/// Gated on the eval — a lesson the bake didn't take stays as the safety net.
/// </summary>
public static class Graduation
{
    public sealed record Result(string LessonId, string Warning, double ScoreWithoutLesson, bool Graduated);

    public static async Task<IReadOnlyList<Result>> RunAsync(
        string agent, SemanticLessonStore store,
        Func<Lesson, CancellationToken, Task<double>> scoreOnBakedWithoutLesson,
        double passThreshold, bool evict, CancellationToken ct = default)
    {
        var results = new List<Result>();
        var verified = (await store.AllAsync(ct)).Where(l => l.Agent == agent && l.Trust == Trust.Verified).ToList();
        foreach (var l in verified)
        {
            var score = await scoreOnBakedWithoutLesson(l, ct);
            var grad = score >= passThreshold;
            results.Add(new Result(l.Id, l.Warning, score, grad));
            if (grad && evict) await store.RemoveAsync(l.Id, ct);
        }
        return results;
    }
}
