using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AIAssistant.Harness;

namespace Compare;

// A demo task in some domain, with the reward key kept OUT of the model's prompt (so it can't cheat).
public sealed record DemoTask(string Prompt, string[] Sources, string[] Accept, string Note);

// One domain the comparison can run. Each provides sample tasks, a harness agent (reward + generation),
// and the "bare" baseline — the honest playground: the SAME task, one shot, no loop / memory / tools.
public interface IDomain
{
    string Key { get; }
    string Title { get; }
    string Blurb { get; }        // what the harness adds here
    string Sector { get; }
    bool SelfVerify { get; }     // wire an LLM critic for this domain
    int Samples { get; }         // best-of-N per round
    IReadOnlyList<DemoTask> Tasks { get; }
    IAgent NewAgent(DemoTask t);
    Task<string> BareAsync(DemoTask t, CancellationToken ct);
}

// Shared helpers ---------------------------------------------------------------
internal static class Llm
{
    public static Task<string> Plain(string system, string user, double temp, CancellationToken ct)
        => ToolLoop.Enabled ? ToolLoop.CompleteAsync(system, user, temp, ct) : System.Threading.Tasks.Task.FromResult("(set AGENT_LLM_* to run live)");

    public static string WithLessons(string system, IReadOnlyList<Lesson> lessons, string? critique)
    {
        var sb = new StringBuilder(system);
        if (lessons.Count > 0) { sb.Append("\n\nLessons you have learned (apply them):"); foreach (var l in lessons) sb.Append("\n• ").Append(l.Warning); }
        if (!string.IsNullOrWhiteSpace(critique)) sb.Append("\n\nYour previous answer was judged wrong. Fix this and answer again:\n").Append(critique);
        return sb.ToString();
    }
}

// ── 1. Factual QA — the harness LEARNS to ground with web_search ──────────────
// Bare answers from (stale) memory. In the harness, attempt-1 also answers from memory → the reward
// fails it → the critique tells it to verify → it calls web_search → passes → learns "verify with search".
// Run again: the lesson is injected, so attempt-1 searches first try. Compounding + learned tool use.
public sealed class QaDomain : IDomain
{
    public string Key => "qa"; public string Title => "Factual QA";
    public string Blurb => "grounding: the harness learns to verify with web_search instead of trusting stale memory";
    public string Sector => "qa"; public bool SelfVerify => false; public int Samples => 1;
    public IReadOnlyList<DemoTask> Tasks => new[]
    {
        new DemoTask("Who became the CEO of Berkshire Hathaway in 2026?", Array.Empty<string>(), new[]{"abel"}, "post-cutoff fact"),
        new DemoTask("What is the capital city of Australia?", Array.Empty<string>(), new[]{"canberra"}, "commonly mis-answered"),
    };
    public Task<string> BareAsync(DemoTask t, CancellationToken ct) =>
        Llm.Plain("Answer in one short sentence. If unsure, give your best guess — do not refuse.", t.Prompt, 0, ct);
    public IAgent NewAgent(DemoTask t) => new QaAgent(t);

    sealed class QaAgent : IAgent
    {
        private readonly DemoTask _t; public QaAgent(DemoTask t) => _t = t;
        public string Id => "cmp-qa";
        public async Task<string> GenerateAsync(AgentContext ctx, IReadOnlyList<Lesson> lessons, string? critique, string? prior, int attempt, CancellationToken ct)
        {
            var mustSearch = (critique?.Contains("web_search") ?? false) || lessons.Any(l => l.Warning.Contains("web_search"));
            if (mustSearch && ToolLoop.Enabled)
                return await ToolLoop.RunAsync("You are a careful assistant. Use web_search to verify the fact, then answer in one short sentence.",
                    _t.Prompt, new ITool[] { new WebSearchTool() }, maxSteps: 4, ct: ct);
            return await Llm.Plain(Llm.WithLessons("Answer in one short sentence. If unsure, give your best guess.", lessons, critique), _t.Prompt, 0, ct);
        }
        public Reward Evaluate(string draft, AgentContext ctx)
        {
            var ok = _t.Accept.Any(k => draft.ToLowerInvariant().Contains(k));
            return new Reward(ok, ok ? 1 : 0, new Dictionary<string, double> { ["correct"] = ok ? 1 : 0 },
                ok ? new HashSet<string>() : new HashSet<string> { "WRONG_FACT" },
                ok ? "" : "Your answer looks incorrect. Call web_search to verify the specific fact, then answer again.");
        }
        public Lesson? LessonFor(string trigger, AgentContext ctx) => trigger != "WRONG_FACT" ? null : new Lesson
        {
            Id = "cmp-qa|qa|WRONG_FACT", Agent = "cmp-qa", Sector = "qa", Trigger = "WRONG_FACT",
            Condition = "a factual question about names, dates, or current facts",
            Warning = "Before answering a factual question, call web_search to verify — do not trust memory, which can be stale.",
        };
    }
}

// ── 2. Reasoning — best-of-N + self-verify lift a rushed cheap model ──────────
// Bare answers in one rushed shot (often wrong). The harness samples several step-by-step attempts, an
// LLM critic checks the work, and the reward keeps only a correct FINAL. Learns "show the steps, verify".
public sealed class ReasonDomain : IDomain
{
    public string Key => "reason"; public string Title => "General reasoning";
    public string Blurb => "best-of-N + self-verify: the cheap model works a problem it rushes wrong on its own";
    public string Sector => "reason"; public bool SelfVerify => true; public int Samples => 3;
    public IReadOnlyList<DemoTask> Tasks => new[]
    {
        new DemoTask("A bat and a ball cost $1.10 in total. The bat costs $1.00 more than the ball. How much does the ball cost, in cents?", Array.Empty<string>(), new[]{"5 cent","5cent","$0.05","0.05","5 c"}, "classic trap (answer 5, not 10)"),
        new DemoTask("If it takes 5 machines 5 minutes to make 5 widgets, how many minutes for 100 machines to make 100 widgets?", Array.Empty<string>(), new[]{"5 minute","5 min","5min"," 5 "}, "rate trap (answer 5)"),
    };
    public Task<string> BareAsync(DemoTask t, CancellationToken ct) =>
        Llm.Plain("Answer with just the final answer, briefly.", t.Prompt, 0, ct);
    public IAgent NewAgent(DemoTask t) => new ReasonAgent(t);

    sealed class ReasonAgent : IAgent
    {
        private readonly DemoTask _t; public ReasonAgent(DemoTask t) => _t = t;
        public string Id => "cmp-reason";
        public Task<string> GenerateAsync(AgentContext ctx, IReadOnlyList<Lesson> lessons, string? critique, string? prior, int attempt, CancellationToken ct)
            => Llm.Plain(Llm.WithLessons("Solve the problem. Think step by step, then output the answer on its own final line starting with 'FINAL:'.", lessons, critique),
                _t.Prompt, attempt == 0 ? 0.2 : 0.8, ct); // vary temperature across best-of-N samples
        public Reward Evaluate(string draft, AgentContext ctx)
        {
            var final = Regex.Match(draft, @"FINAL:\s*(.+)", RegexOptions.IgnoreCase | RegexOptions.RightToLeft);
            var tail = (final.Success ? final.Groups[1].Value : draft).ToLowerInvariant();
            var ok = _t.Accept.Any(k => tail.Contains(k));
            return new Reward(ok, ok ? 1 : 0, new Dictionary<string, double> { ["correct"] = ok ? 1 : 0 },
                ok ? new HashSet<string>() : new HashSet<string> { "WRONG_ANSWER" },
                ok ? "" : "That FINAL answer is wrong. Re-read the problem, watch for the trap, redo the steps carefully.");
        }
        public Lesson? LessonFor(string trigger, AgentContext ctx) => trigger != "WRONG_ANSWER" ? null : new Lesson
        {
            Id = "cmp-reason|reason|WRONG_ANSWER", Agent = "cmp-reason", Sector = "reason", Trigger = "WRONG_ANSWER",
            Condition = "a word problem that looks simple but has a trap",
            Warning = "Do not answer word problems from intuition. Write the equations, solve step by step, and re-check the final number before answering.",
        };
    }
}

// ── 3. Value-investing (moat) — grounding gate, learned run-to-run ────────────
// Sources are labelled [S1]..[Sn]. A claim must carry a valid [S#] tag and must not cite a source that
// wasn't provided. Bare (one shot) tends to omit citations or invent one; the harness's reward catches it
// and it learns "cite only provided [S#]" — so run 2 is grounded first try.
public sealed class MoatDomain : IDomain
{
    public string Key => "moat"; public string Title => "Value-investing (moat)";
    public string Blurb => "grounding gate: every claim must cite only the provided [S#] sources";
    public string Sector => "consumer_staples"; public bool SelfVerify => false; public int Samples => 1;
    public IReadOnlyList<DemoTask> Tasks => new[]
    {
        new DemoTask("Assess the economic moat in two sentences.",
            new[]{ "37% market share, #1 in the category for 10 years", "gross margin 42%, ~10pts above peers", "distribution to 250,000 outlets nationwide" },
            Array.Empty<string>(), "must cite [S1]/[S2]/[S3], never [S4]"),
    };
    public Task<string> BareAsync(DemoTask t, CancellationToken ct) => Llm.Plain(SysFor(t, Array.Empty<Lesson>(), null), t.Prompt, 0, ct);
    public IAgent NewAgent(DemoTask t) => new MoatAgent(t);

    static string SysFor(DemoTask t, IReadOnlyList<Lesson> lessons, string? critique)
    {
        var sb = new StringBuilder("You are an equity analyst. Write a 2-sentence moat assessment. Support every claim with a citation tag [S1], [S2], … that refers ONLY to the sources below.\n\nSOURCES:");
        for (var i = 0; i < t.Sources.Length; i++) sb.Append($"\n[S{i + 1}] {t.Sources[i]}");
        return Llm.WithLessons(sb.ToString(), lessons, critique);
    }

    sealed class MoatAgent : IAgent
    {
        private readonly DemoTask _t; public MoatAgent(DemoTask t) => _t = t;
        public string Id => "cmp-moat";
        public Task<string> GenerateAsync(AgentContext ctx, IReadOnlyList<Lesson> lessons, string? critique, string? prior, int attempt, CancellationToken ct)
            => Llm.Plain(SysFor(_t, lessons, critique), _t.Prompt, 0, ct);
        public Reward Evaluate(string draft, AgentContext ctx)
        {
            var cited = Regex.Matches(draft, @"\[S(\d+)\]").Select(m => int.Parse(m.Groups[1].Value)).ToList();
            var valid = cited.Where(n => n >= 1 && n <= _t.Sources.Length).ToList();
            var invalid = cited.Where(n => n < 1 || n > _t.Sources.Length).ToList();
            var fails = new HashSet<string>();
            if (valid.Count == 0) fails.Add("UNCITED");
            if (invalid.Count > 0) fails.Add("BAD_CITATION");
            var ok = fails.Count == 0;
            var crit = ok ? "" : (valid.Count == 0 ? "Support each claim with a [S#] tag from the provided sources. " : "")
                              + (invalid.Count > 0 ? $"You cited a source that does not exist ([S{invalid[0]}]); cite only [S1]–[S{_t.Sources.Length}]." : "");
            return new Reward(ok, ok ? 1 : Math.Max(0, 0.5 - 0.25 * fails.Count),
                new Dictionary<string, double> { ["grounded"] = ok ? 1 : 0 }, fails, crit);
        }
        public Lesson? LessonFor(string trigger, AgentContext ctx) => new Lesson
        {
            Id = $"cmp-moat|consumer_staples|{trigger}", Agent = "cmp-moat", Sector = "consumer_staples", Trigger = trigger,
            Condition = "writing an analysis from a fixed set of provided sources",
            Warning = "Support every claim with a [S#] tag, and cite ONLY the sources you were given — never invent a citation.",
        };
    }
}

public static class Registry
{
    public static readonly IReadOnlyList<IDomain> All = new IDomain[] { new QaDomain(), new ReasonDomain(), new MoatDomain() };
    public static IDomain? Get(string key) => All.FirstOrDefault(d => d.Key == key);
}
