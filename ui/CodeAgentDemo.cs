using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AIAssistant.Harness;

// The code-agent A/B, wired for the UI (/api/codeagent). Real reward: an LLM writes Python and the reward
// RUNS the unit tests. The learnable signal is a house convention it doesn't know until it fails — "round
// floats to 2 decimals". With shared memory it learns once and one-shots the rest; with fresh memory it
// re-fails every problem. Proves the same harness learns in a second, non-finance domain.
static class CodeAgentDemo
{
    private static readonly CodeProb[] Problems =
    {
        new("average",    "It returns the arithmetic mean of the list of numbers `nums`.",           "[{\"args\":[[1,1,2]],\"exp\":1.33},{\"args\":[[2,3,5]],\"exp\":3.33}]"),
        new("growth_pct", "It returns the percentage growth from `old` to `new`: (new-old)/old*100.", "[{\"args\":[7,10],\"exp\":42.86},{\"args\":[30,50],\"exp\":66.67}]"),
        new("ratio",      "It returns `a` divided by `b`.",                                           "[{\"args\":[10,3],\"exp\":3.33},{\"args\":[22,7],\"exp\":3.14}]"),
        new("portion",    "It returns `part` as a percentage of `total`: part/total*100.",            "[{\"args\":[1,3],\"exp\":33.33},{\"args\":[2,7],\"exp\":28.57}]"),
        new("safe_div",   "It returns `a` divided by `b`.",                                           "[{\"args\":[100,7],\"exp\":14.29},{\"args\":[5,6],\"exp\":0.83}]"),
    };

    public static async Task<JsonObject> RunAbAsync()
    {
        try
        {
            if (!CodeCx.Enabled) return new JsonObject { ["ok"] = false, ["error"] = "Set AGENT_LLM_* — the code agent needs a live model." };
            if (!CodeCx.HasPython()) return new JsonObject { ["ok"] = false, ["error"] = "Python not found on PATH — the test-runner reward needs it." };
            var opt = new HarnessOptions(MaxIters: 3, Threshold: 0.99, RetrieveTopK: 3);
            var w = await RunCond(true, opt);
            var n = await RunCond(false, opt);
            static double FirstRate(JsonArray a) => a.Count == 0 ? 0 : (double)a.Count(x => x!["first"]!.GetValue<double>() >= 0.99) / a.Count;
            static double AvgIters(JsonArray a) => a.Count == 0 ? 0 : a.Average(x => x!["iters"]!.GetValue<int>());
            return new JsonObject
            {
                ["ok"] = true, ["withMemory"] = w, ["noMemory"] = n,
                ["summary"] = new JsonObject { ["withFirst"] = FirstRate(w), ["noFirst"] = FirstRate(n), ["withIters"] = AvgIters(w), ["noIters"] = AvgIters(n) },
            };
        }
        catch (Exception e) { return new JsonObject { ["ok"] = false, ["error"] = e.Message }; }
    }

    private static async Task<JsonArray> RunCond(bool memory, HarnessOptions opt)
    {
        var arr = new JsonArray();
        var shared = memory ? new SemanticLessonStore(CodeCx.Fresh()) : null;
        foreach (var p in Problems)
        {
            var store = memory ? shared! : new SemanticLessonStore(CodeCx.Fresh());
            var harness = new AgentHarness(store, () => "2026-07-29");
            var ctx = new AgentContext
            {
                Ticker = p.Func,
                Features = new AgentFeatures("python", Array.Empty<string>(), p.Spec),
                Input = new JsonObject { ["func"] = p.Func, ["spec"] = p.Spec, ["tests"] = JsonNode.Parse(p.TestsJson) },
                AllowedSources = Array.Empty<string>(),
            };
            var o = await harness.RunAsync(new CodeGenAgent(), ctx, opt, default);
            arr.Add(new JsonObject { ["func"] = p.Func, ["first"] = o.FirstScore, ["iters"] = o.Iterations, ["final"] = o.Best.Score });
        }
        return arr;
    }
}

record CodeProb(string Func, string Spec, string TestsJson);

sealed class CodeGenAgent : IAgent
{
    public string Id => "codegen";

    public async Task<string> GenerateAsync(AgentContext ctx, IReadOnlyList<Lesson> lessons, string? critique, string? prior, int attempt, CancellationToken ct)
    {
        var func = ctx.Input["func"]!.GetValue<string>();
        var sb = new StringBuilder($"Write a Python function named `{func}`. {ctx.Input["spec"]!.GetValue<string>()}");
        if (lessons.Count > 0) { sb.Append("\n\nHouse rules (follow them):"); foreach (var l in lessons) sb.Append("\n- " + l.Warning); }
        if (critique is not null) sb.Append($"\n\nYour previous solution failed:\n{critique}\nReturn the corrected full function.");
        return CodeCx.ExtractCode(await CodeCx.Complete("You are a senior Python developer. Return ONLY the function definition — no markdown, no prose.", sb.ToString(), ct));
    }

    public Reward Evaluate(string draft, AgentContext ctx)
    {
        var func = ctx.Input["func"]!.GetValue<string>();
        var tests = (JsonArray)ctx.Input["tests"]!;
        var (ran, res, err) = CodeCx.RunPy(draft, func, tests);
        if (!ran)
            return new Reward(false, 0, new Dictionary<string, double> { ["tests"] = 0 }, new HashSet<string> { "SYNTAX" }, $"code did not run: {err.Split('\n').LastOrDefault(l => l.Trim().Length > 0)}");
        var passed = res.Count(x => x.Ok);
        var score = res.Count == 0 ? 0 : (double)passed / res.Count;
        var triggers = new HashSet<string>();
        var crit = new List<string>();
        for (var i = 0; i < res.Count; i++)
        {
            if (res[i].Ok) continue;
            var args = tests[i]!["args"]!.ToJsonString();
            var (got, exp) = (res[i].Got, res[i].Exp);
            if (got is not null && Math.Abs(Math.Round(got.Value, 2) - exp) < 1e-9)
            { triggers.Add("ROUNDING"); crit.Add($"{func}{args} returned {got} but expected {exp} — round floats to 2 decimals."); }
            else
            { triggers.Add("FORMULA"); crit.Add($"{func}{args} returned {(got?.ToString() ?? "error")} but expected {exp}."); }
        }
        if (crit.Count == 0) crit.Add("all tests pass.");
        return new Reward(true, Math.Round(score, 4), new Dictionary<string, double> { ["tests"] = score }, triggers, string.Join("\n", crit));
    }

    public Lesson? LessonFor(string trigger, AgentContext ctx) => trigger == "ROUNDING"
        ? new Lesson { Id = "codegen|python|ROUNDING", Agent = Id, Sector = "python", Trigger = "ROUNDING", Type = LessonType.Strategy,
            Condition = "a function returns a floating-point result",
            Warning = "House style: round every floating-point result to exactly 2 decimal places, e.g. `return round(x, 2)`.", LearnedFrom = ctx.Ticker }
        : null;
}

static class CodeCx
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private static string? Env(string k) => Environment.GetEnvironmentVariable(k);
    public static bool Enabled => !string.IsNullOrWhiteSpace(Env("AGENT_LLM_BASE_URL"));
    public static string Fresh() => Path.Combine(Path.GetTempPath(), $"codeui_{Guid.NewGuid():N}.json");

    public static bool HasPython()
    {
        try { using var p = Process.Start(new ProcessStartInfo("python", "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false }); p!.WaitForExit(5000); return true; }
        catch { return false; }
    }

    public static string ExtractCode(string s)
    {
        s = s.Trim();
        var m = Regex.Match(s, "```(?:python)?\\s*([\\s\\S]*?)```");
        return m.Success ? m.Groups[1].Value.Trim() : s;
    }

    public static (bool Ran, List<(bool Ok, double? Got, double Exp)> Res, string Err) RunPy(string code, string func, JsonArray tests)
    {
        var testsJson = tests.ToJsonString();
        var runner = $$"""
{{code}}

import json
_tests = {{testsJson}}
_f = globals().get("{{func}}")
_res = []
for _t in _tests:
    try:
        _g = _f(*_t["args"])
        _ok = (_g is not None) and (not isinstance(_g, bool)) and (abs(float(_g) - _t["exp"]) < 1e-6)
        _res.append({"ok": _ok, "got": (float(_g) if isinstance(_g, (int, float)) and not isinstance(_g, bool) else None), "exp": _t["exp"]})
    except Exception as _e:
        _res.append({"ok": False, "got": None, "exp": _t["exp"]})
print("RESULTS:" + json.dumps(_res))
""";
        var tmp = Path.Combine(Path.GetTempPath(), $"cgui_{Guid.NewGuid():N}.py");
        File.WriteAllText(tmp, runner);
        try
        {
            using var p = Process.Start(new ProcessStartInfo("python", $"\"{tmp}\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
            var outp = p.StandardOutput.ReadToEnd(); var errp = p.StandardError.ReadToEnd(); p.WaitForExit(15000);
            var line = outp.Split('\n').FirstOrDefault(l => l.StartsWith("RESULTS:"));
            if (line is null) return (false, new(), errp);
            var arr = (JsonArray)JsonNode.Parse(line["RESULTS:".Length..])!;
            var res = arr.Select(n => (n!["ok"]!.GetValue<bool>(), n["got"] is null ? (double?)null : n["got"]!.GetValue<double>(), n["exp"]!.GetValue<double>())).ToList();
            return (true, res, "");
        }
        catch (Exception e) { return (false, new(), e.Message); }
        finally { try { File.Delete(tmp); } catch { } }
    }

    public static async Task<string> Complete(string sys, string user, CancellationToken ct)
    {
        var url = Env("AGENT_LLM_BASE_URL")!.TrimEnd('/') + "/chat/completions";
        var ver = Env("AGENT_LLM_API_VERSION");
        if (!string.IsNullOrWhiteSpace(ver)) url += (url.Contains('?') ? "&" : "?") + "api-version=" + ver;
        var payload = new JsonObject
        {
            ["model"] = Env("AGENT_LLM_MODEL") is { Length: > 0 } m ? m : "gpt-4o-mini",
            ["temperature"] = 0, ["max_tokens"] = 500,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = sys }, new JsonObject { ["role"] = "user", ["content"] = user }),
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json") };
        var key = Env("AGENT_LLM_API_KEY");
        if (!string.IsNullOrWhiteSpace(key))
        {
            if ((Env("AGENT_LLM_AUTH") ?? "bearer").ToLowerInvariant() == "api-key") req.Headers.Add("api-key", key);
            else req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
        using var resp = await Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
    }
}
