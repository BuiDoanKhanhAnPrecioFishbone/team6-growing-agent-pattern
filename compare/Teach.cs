using System.Text.Json.Nodes;
using AIAssistant.Harness;

namespace Compare;

// The human-in-the-loop teaching flow: a person states a rule, it's stored as a VERIFIED lesson, and the
// agent applies it on the next run — a real "teach → better result" loop. This is how a growing agent learns
// from your experts (the Omnia story), live in the UI. Lessons persist in their own store so Reset is clean.
public static class TeachRun
{
    private static readonly SemanticLessonStore Store = new(Path.Combine(Path.GetTempPath(), "compare-teach.json"));
    private const string Agent = "teach-advisor", Sector = "advisory";
    private const string Task =
        "Write a short investment note (2–3 sentences) recommending whether to buy shares of \"VNM\", a consumer-staples company.";

    public static async Task<object> RunAsync(string? teach, bool reset, CancellationToken ct)
    {
        if (reset) Store.Clear();
        if (!string.IsNullOrWhiteSpace(teach))
            await Store.WriteAsync(new Lesson
            {
                Id = $"{Agent}|{Sector}|{Guid.NewGuid():N}", Agent = Agent, Sector = Sector, Trigger = "taught",
                Condition = "writing an investment note", Warning = teach.Trim(),
                Trust = Trust.Verified,   // a human taught it → trusted immediately
                Date = DateTime.UtcNow.ToString("yyyy-MM-dd"), LearnedFrom = "human reviewer",
            }, ct);

        var lessons = (await Store.AllAsync()).Where(l => l.Agent == Agent && l.Trust != Trust.Quarantined).ToList();

        string output;
        if (!ToolLoop.Enabled) output = "(set AGENT_LLM_* to run live)";
        else
        {
            var sys = "You are an equity analyst writing a brief, professional investment note.";
            if (lessons.Count > 0)
                sys += "\n\nRules your reviewers have taught you — follow EVERY one:" + string.Concat(lessons.Select(l => "\n• " + l.Warning));
            output = await ToolLoop.CompleteAsync(sys, Task, 0.2, ct);
        }

        return new { task = Task, output, lessons = lessons.Select(l => new { warning = l.Warning }).ToList() };
    }
}
