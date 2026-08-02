using System.Text.Json.Nodes;
using AIAssistant.Harness;

// ─────────────────────────────────────────────────────────────────────────────
// personalize — the same free-data loop, scoped, gives you PERSONALIZED agents. Memory
// has an Owner: global (shared), tenant (team), user (personal). Retrieval merges the
// scopes that apply and ranks the most specific first. So one mechanism delivers BOTH
// org-wide compounding AND per-user personalization — no per-user fine-tuning.
//
//   Part 1 · scoping isolates + hierarchies: Alice sees her rule + global, never Bob's;
//            a brand-new user inherits global on day one (no cold-start void).
//   Part 2 · the harness LEARNS scoped: a lesson learned in Alice's session is stamped
//            user:alice and never leaks to Bob.
// Offline & deterministic.
// ─────────────────────────────────────────────────────────────────────────────

int pass = 0, fail = 0;
void Check(string name, bool ok) { Console.WriteLine($"   [{(ok ? "PASS" : "FAIL")}] {name}"); if (ok) pass++; else fail++; }
const string Agent = "greeter", Sector = "greet";

AgentFeatures For(string user) => new(Sector, Array.Empty<string>(), Situation: "", User: user);

Console.WriteLine("personalize — one loop, scoped: shared learning AND per-user personalization\n");

// ── Part 1: explicit scoped lessons — isolation, hierarchy, cold-start ──
Console.WriteLine("Part 1 · scoping isolates and hierarchies");
var path1 = Path.Combine(Path.GetTempPath(), "personalize-1.json"); File.Delete(path1);
var store = new SemanticLessonStore(path1);

Lesson L(string owner, string warning, string trig) => new()
{
    Id = string.IsNullOrEmpty(owner) ? $"{Agent}|{Sector}|{trig}" : $"{Agent}|{Sector}|{trig}@{owner}",
    Agent = Agent, Sector = Sector, Trigger = trig, Condition = "greeting a user", Warning = warning,
    Owner = owner, Trust = Trust.Verified, Date = "2026-08-02",
};
await store.WriteAsync(L(Scope.Global,        "Keep it under 12 words.",      "concise"));   // everyone
await store.WriteAsync(L(Scope.User("alice"), "Use a formal salutation.",     "tone"));      // Alice only
await store.WriteAsync(L(Scope.User("bob"),   "End with a friendly emoji.",   "tone"));      // Bob only

var alice = await store.RetrieveAsync(Agent, For("alice"), 5, default);
var bob   = await store.RetrieveAsync(Agent, For("bob"),   5, default);
var carol = await store.RetrieveAsync(Agent, For("carol"), 5, default);   // brand-new user

Console.WriteLine("     alice sees: " + string.Join(" | ", alice.Select(l => l.Warning)));
Console.WriteLine("     bob   sees: " + string.Join(" | ", bob.Select(l => l.Warning)));
Console.WriteLine("     carol sees: " + string.Join(" | ", carol.Select(l => l.Warning)));

Check("Alice gets her own rule + the global one", alice.Any(l => l.Warning.Contains("formal")) && alice.Any(l => l.Warning.Contains("under 12")));
Check("Alice does NOT see Bob's rule (no leakage)", !alice.Any(l => l.Warning.Contains("emoji")));
Check("Bob gets his own rule + the global one", bob.Any(l => l.Warning.Contains("emoji")) && bob.Any(l => l.Warning.Contains("under 12")));
Check("Bob does NOT see Alice's rule", !bob.Any(l => l.Warning.Contains("formal")));
Check("a brand-new user inherits ONLY global (no cold-start void)", carol.Count == 1 && carol[0].Warning.Contains("under 12"));
Check("a user's own rule ranks above the global one (specificity)", alice[0].Warning.Contains("formal"));

// personalized OUTPUT: the same agent, three users, three answers
Console.WriteLine("\n     personalized output (same agent, same task):");
Console.WriteLine($"       alice → {new GreetAgent().Gen(alice)}");
Console.WriteLine($"       bob   → {new GreetAgent().Gen(bob)}");
Console.WriteLine($"       carol → {new GreetAgent().Gen(carol)}");

// ── Part 2: the harness learns scoped ──
Console.WriteLine("\nPart 2 · the harness learns a PERSONAL lesson");
var path2 = Path.Combine(Path.GetTempPath(), "personalize-2.json"); File.Delete(path2);
var store2 = new SemanticLessonStore(path2);
var harness = new AgentHarness(store2);
var opt = new HarnessOptions(MaxIters: 3, Threshold: 1.0, RetrieveTopK: 3, Samples: 1);

var ctxAlice = new AgentContext
{
    Ticker = "greet", Features = new AgentFeatures(Sector, Array.Empty<string>(), "greeting a user", User: "alice"),
    Input = new JsonObject { ["task"] = "greet" }, AllowedSources = Array.Empty<string>(),
};
var o = await harness.RunAsync(new GreetAgent(), ctxAlice, opt, default);
var learned = (await store2.AllAsync()).FirstOrDefault(l => l.Trigger.StartsWith("concise"));
Console.WriteLine($"     learned in Alice's session: id={learned?.Id}");
Check("the learned lesson is scoped to Alice", learned?.Owner == "user:alice");
Check("its id is namespaced by owner (no cross-user collision)", learned?.Id.Contains("@user:alice") == true);
var forBob = await store2.RetrieveAsync(Agent, For("bob"), 5, default);
Check("Alice's learned lesson does NOT leak to Bob", !forBob.Any(l => l.Owner == "user:alice"));
var forAlice = await store2.RetrieveAsync(Agent, For("alice"), 5, default);
Check("Alice recalls her own learned lesson next time", forAlice.Any(l => l.Owner == "user:alice"));

Console.WriteLine($"\n{pass} passed, {fail} failed.");
Console.WriteLine(fail == 0
    ? "\nVerdict: one scoped loop — shared lessons compound for everyone, personal lessons make it yours, nothing leaks."
    : "\nVerdict: scoping regressed — investigate.");
Environment.Exit(fail == 0 ? 0 : 1);

// A greeting agent whose output follows its injected lessons — and whose reward is "be concise",
// so a verbose first draft is fixed on revise and learned as a lesson (scoped to the session's user).
sealed class GreetAgent : IAgent
{
    public string Id => "greeter";

    public string Gen(IReadOnlyList<Lesson> lessons)
    {
        var formal = lessons.Any(l => l.Warning.Contains("formal"));
        var emoji  = lessons.Any(l => l.Warning.Contains("emoji"));
        var concise = lessons.Any(l => l.Warning.Contains("under 12"));
        var g = formal ? "Dear user, welcome to the platform" : "Hey, welcome to the platform";
        if (emoji) g += " 👋";
        if (!concise) g += " — we are truly delighted to have you here and hope you enjoy every single feature we built.";
        return g;
    }

    public Task<string> GenerateAsync(AgentContext ctx, IReadOnlyList<Lesson> lessons, string? critique, string? prior, int attempt, CancellationToken ct)
    {
        var concise = lessons.Any(l => l.Warning.Contains("under 12")) || (critique?.Contains("12") ?? false);
        var g = "Hey, welcome to the platform";
        if (!concise) g += " — we are truly delighted to have you here and hope you enjoy every single feature we built.";
        return Task.FromResult(g);
    }

    public Reward Evaluate(string draft, AgentContext ctx)
        => Checks.Of(("concise (under 12 words)", draft.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length <= 12));

    public Lesson? LessonFor(string trigger, AgentContext ctx) => new()
    {
        Id = $"{Id}|greet|{trigger}", Agent = Id, Sector = "greet", Trigger = trigger,
        Condition = "greeting a user", Warning = "Keep it under 12 words.", Type = LessonType.Strategy,
    };
}
