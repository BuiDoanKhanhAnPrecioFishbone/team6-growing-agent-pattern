using AIAssistant.Harness;

// D8-10: watch the agent USE tools. Seed a lesson, expose memory_search + margin_of_safety, then ask a
// question that needs both. The model should call the tools, then answer. Read-only tools run freely.
if (!ToolLoop.Enabled) { Console.WriteLine("set AGENT_LLM_* to run the tool loop (needs a live model)."); return; }

var storePath = Path.Combine(Path.GetTempPath(), "tooltest.json");
if (File.Exists(storePath)) File.Delete(storePath);
var store = new SemanticLessonStore(storePath);
await store.WriteAsync(new Lesson { Id = "disc", Agent = "advisor", Sector = "vn", Trust = Trust.Verified,
    Condition = "buy recommendation", Warning = "Every buy recommendation must include the not-advice disclaimer." });

var tools = new ITool[] { new MemorySearchTool(store, "advisor", "vn"), new ComputeMosTool() };

const string sys = "You are an investment assistant. Use margin_of_safety to compute MoS, and memory_search " +
                   "to recall any of your own rules that apply, THEN give a one-line recommendation.";
const string user = "Intrinsic value is 23000 VND and the price is 18100 VND. Give a one-line buy/hold recommendation.";

try
{
    var answer = await ToolLoop.RunAsync(sys, user, tools,
        onCall: (name, result) => Console.WriteLine($"  ↪ tool {name} → {result.Replace("\n", " | ")}"));
    Console.WriteLine($"\nFINAL: {answer}");
}
catch (Exception e) { Console.WriteLine($"tool loop error (key rotated?): {e.Message}"); }
