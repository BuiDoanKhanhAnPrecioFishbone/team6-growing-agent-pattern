using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AIAssistant.Harness;

namespace Compare;

// A demo task in some domain, with the reward key kept OUT of the model's prompt (so it can't cheat).
public sealed record DemoTask(string Prompt, string[] Sources, string[] Accept, string Note);

// The Figma target as a data URL (loaded from the local, gitignored wwwroot/target.png at startup).
// When present, the UI agent GROUNDS generation in the actual image (multimodal). Null → text-only.
public static class UiTarget { public static string? ImageDataUrl; }

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
    bool ThreeWay => false;   // show a 3rd "learned" column + a design-match score + the lesson list (UI)
    int MaxIters => 3;        // per-domain cap on regenerations
    ICritic? Critic => SelfVerify ? new LlmCritic() : null;  // self-verify critic (UI overrides with a vision judge)
    int Elements => 0;        // for a checklist domain (UI): how many design tokens the score is out of
}

// Layer 2 — the vision judge: SEES the target design and the candidate's HTML, and lists concrete diffs to
// fix (visual AND behavioural) so the loop refines toward the actual design instead of a keyword checklist.
public sealed class VisionCritic : ICritic
{
    public async Task<string?> CritiqueAsync(AgentContext ctx, string draft, Reward reward, CancellationToken ct)
    {
        if (UiTarget.ImageDataUrl is null || !ToolLoop.Enabled) return null;
        const string sys =
            "You are a senior UI engineer reviewing an implementation against a TARGET design (the attached image). "
            + "Compare the CANDIDATE HTML/CSS to the target and list ONLY concrete, actionable fixes so it matches the "
            + "target — visual (colors, spacing, the star widget's look — bare stars, NOT boxed; button styles and order) "
            + "AND behavioural (hover/click on the stars sets the rating; the Save button is disabled until a rating and "
            + "review exist). Terse bullet points. If it already matches the target well, reply with exactly: OK";
        try
        {
            var verdict = (await ToolLoop.CompleteVisionAsync(sys, "CANDIDATE HTML:\n" + draft, UiTarget.ImageDataUrl, 0, ct)).Trim();
            return verdict.Length == 0 || verdict.TrimStart('#', '*', '-', ' ', '`').StartsWith("OK", StringComparison.OrdinalIgnoreCase)
                ? null : verdict;
        }
        catch { return null; } // a critic failure must never break the loop
    }
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
    // Playground baseline: the facts, but the NAIVE ask — no citation scaffolding. The harness's value is
    // that it enforces grounding (and learns to), so the bare draft typically omits the [S#] tags.
    public Task<string> BareAsync(DemoTask t, CancellationToken ct)
    {
        var sb = new StringBuilder("You are an equity analyst. Write a 2-sentence economic-moat assessment based on these facts:\n");
        foreach (var s in t.Sources) sb.Append("- ").Append(s).Append('\n');
        return Llm.Plain(sb.ToString(), t.Prompt, 0, ct);
    }
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

// ── 4. UI from a design — the harness LEARNS the exact Figma design tokens ───────────────────────
// The harness gets only a MODERATE brief (structure). The exact tokens of the Figma card — the violet
// accent, the star widget, the speech-bubble icon, the button labels, the lavender background, the
// placeholder — are NOT told; the fidelity reward checks each and the harness learns them as lessons. So:
// bare (naive) < harness cold (spec, no lessons) < harness learned (matches the design). Three-way, with a
// design-match score and the growing lesson list.
public sealed class UiDomain : IDomain
{
    public string Key => "ui"; public string Title => "UI from a design";
    public string Blurb => "reproduce a Figma card — the harness learns the exact colors, widgets and labels. Score = design elements matched (a checklist, not a pixel diff)";
    public string Sector => "frontend"; public bool SelfVerify => false; public int Samples => 1;
    public bool ThreeWay => false;  // two columns: playground vs the learned harness (a cold column ≈ bare, only confusing)
    public int MaxIters => 2;       // cap regenerations — each is a full HTML page
    public ICritic? Critic => new VisionCritic();  // layer 2: the loop refines against the actual image
    public int Elements => Fidelity.Length;        // the score is out of this many design tokens

    // Fidelity to the Figma frame: (trigger, keywords that prove the token is present, the lesson to learn).
    static readonly (string Trigger, string[] Kw, string Lesson)[] Fidelity =
    {
        ("VIOLET",      new[]{"#6d28d9","#7c3aed","#6366f1","#5b21b6","#4f46e5","#8b5cf6","#a855f7","#9333ea","#7e22ce","#4338ca","#6b21a8","violet","indigo","purple","rebeccapurple"},
            "Use a violet/indigo accent (about #6d28d9) for the title and the primary button."),
        ("STARS",       new[]{"★","☆","&#9733","&#9734","fa-star"},
            "Show the rating as five stars (★ ☆), not a dropdown or a number field."),
        ("ICON",        new[]{"💬","&#128172","speech-bubble","speech bubble"},
            "Put a small speech-bubble icon (💬) next to the \"Review for Candidate\" title."),
        ("REWRITE",     new[]{"rewrite with ai","rewrite"},
            "The secondary button is labeled \"Rewrite with AI\" (outlined violet, with a sparkle icon)."),
        ("SAVE",        new[]{"save"},
            "The primary button is labeled \"Save\" (solid violet)."),
        ("LAVENDER",    new[]{"lavender","#f5f3ff","#f3f0ff","#ede9fe","#eef2ff","#f8f7ff","#f6f5ff","#faf5ff","#f4f1fd","#f7f5ff","#eee","#f0eef"},
            "Place the white card on a light lavender page background."),
        ("ROUNDED",     new[]{"border-radius"},
            "Use rounded corners (about 16px) on the card and its controls."),
        ("SHADOW",      new[]{"box-shadow"},
            "Give the card a soft drop shadow."),
        ("PLACEHOLDER", new[]{"write your review"},
            "The textarea placeholder reads \"Write your review for this candidate — strengths, concerns, and a hiring recommendation…\"."),
        ("BLOB",        new[]{"radial-gradient","::before","::after"},
            "Add a subtle decorative gradient blob accent behind the card."),
        ("INTERACTIVE", new[]{"onclick","onmouseover","addeventlistener",":hover","cursor:pointer","cursor: pointer"},
            "Make the stars interactive — hover and click set the rating (JS handlers or :hover + cursor:pointer). The stars are bare, with NO bordered box around each one."),
        ("DISABLED",    new[]{"disabled"},
            "The Save button starts DISABLED (greyed) until a rating and a review have been entered."),
    };
    const string Brief =
        "Build a \"Review for Candidate\" review card as a complete, responsive, self-contained HTML document "
        + "with inline <style> CSS: a header with the title, a rating control, a review textarea with a "
        + "placeholder, and two action buttons at the bottom. Return only the HTML.";

    public IReadOnlyList<DemoTask> Tasks => new[] { new DemoTask(Brief, Array.Empty<string>(), Array.Empty<string>(), "Review-for-Candidate card (from Figma)") };
    // Playground baseline: the NAIVE ask — no spec, no enforcement. The harness's value is that it carries
    // the full design spec and a reward that checks it, so bare here comes out sparse/incomplete.
    public Task<string> BareAsync(DemoTask t, CancellationToken ct) =>
        Llm.Plain("You are a front-end engineer. Return only HTML.",
            "Build a \"Review for Candidate\" review card as a small HTML page.", 0, ct);
    public IAgent NewAgent(DemoTask t) => new UiAgent(t);

    static List<string> Misses(string html)
    {
        var h = html.ToLowerInvariant();
        return Fidelity.Where(f => !f.Kw.Any(k => h.Contains(k.ToLowerInvariant()))).Select(f => f.Trigger).ToList();
    }

    sealed class UiAgent : IAgent
    {
        private readonly DemoTask _t; public UiAgent(DemoTask t) => _t = t;
        public string Id => "cmp-ui";
        public Task<string> GenerateAsync(AgentContext ctx, IReadOnlyList<Lesson> lessons, string? critique, string? prior, int attempt, CancellationToken ct)
        {
            // Layer 1 — multimodal grounding: if the Figma target image is available, generate FROM the image.
            if (UiTarget.ImageDataUrl is not null && ToolLoop.Enabled)
                return ToolLoop.CompleteVisionAsync(
                    "You are a senior front-end engineer. Reproduce the UI in the attached image as ONE complete, "
                    + "self-contained, responsive HTML document (inline <style> CSS). Match its exact colors, layout, "
                    + "the star-rating widget, the header icon, the button labels, the button order and their styles as "
                    + "closely as you can. Return only the HTML.",
                    Llm.WithLessons(_t.Prompt, lessons, critique), UiTarget.ImageDataUrl, 0, ct);

            return Llm.Plain(Llm.WithLessons(
                "You are a senior front-end engineer. Build ONE complete, self-contained, responsive HTML document (inline <style> CSS) for the brief, matching the target design exactly. Return only the HTML.",
                lessons, critique), _t.Prompt, 0, ct);
        }
        public Reward Evaluate(string draft, AgentContext ctx)
        {
            var misses = Misses(draft);
            var score = (double)(Fidelity.Length - misses.Count) / Fidelity.Length;
            var ok = misses.Count == 0 && draft.Contains('<');
            var crit = ok ? "" : "To match the target design, fix these: "
                + string.Join(" ", misses.Select(m => Fidelity.First(f => f.Trigger == m).Lesson));
            return new Reward(ok, Math.Round(score, 3), new Dictionary<string, double> { ["fidelity"] = score }, misses.ToHashSet(), crit);
        }
        public Lesson? LessonFor(string trigger, AgentContext ctx)
        {
            var f = Fidelity.FirstOrDefault(x => x.Trigger == trigger);
            return f.Trigger is null ? null : new Lesson
            {
                Id = $"cmp-ui|frontend|{trigger}", Agent = "cmp-ui", Sector = "frontend", Trigger = trigger,
                Condition = "reproducing the Review-for-Candidate card design",
                Warning = f.Lesson,
            };
        }
    }
}

public static class Registry
{
    public static readonly IReadOnlyList<IDomain> All = new IDomain[] { new UiDomain(), new QaDomain(), new ReasonDomain(), new MoatDomain() };
    public static IDomain? Get(string key) => All.FirstOrDefault(d => d.Key == key);
}
