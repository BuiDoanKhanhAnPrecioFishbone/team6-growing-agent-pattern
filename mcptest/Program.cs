using AIAssistant.Harness;
using System.Text.Json.Nodes;

// mcptest — proves the MCP transport: connect to the reference MCP server over stdio, list its tools, and
// call a couple. Any MCP server works; this uses the public "everything" server via npx (downloads once).
//   dotnet run --project mcptest
// Wraps each server tool as an ITool, so from here an agent's tool-loop can use them like any built-in.

// The npx-launched server we demo against (any MCP server works — pass your own command as args).
var serverArgs = args.Length > 0 ? args : new[] { "-y", "@modelcontextprotocol/server-everything", "stdio" };
// On Windows the npm `npx.cmd` shim mis-resolves when launched with redirected stdio; go through cmd.exe.
var (cmd, launch) = OperatingSystem.IsWindows()
    ? ("cmd.exe", new[] { "/c", "npx" }.Concat(serverArgs).ToArray())
    : ("npx", serverArgs);
Console.WriteLine($"connecting: {cmd} {string.Join(' ', launch)}  (first run downloads the server) …\n");

await using var src = await McpToolSource.ConnectStdioAsync(cmd, launch);
Console.WriteLine($"connected — {src.Tools.Count} tools discovered:");
foreach (var t in src.Tools.Take(12))
    Console.WriteLine($"  • {t.Name,-22} readOnly={t.ReadOnly,-5} {Clip(t.Description)}");

var echo = src.Tools.FirstOrDefault(t => t.Name == "echo");
if (echo is not null)
    Console.WriteLine($"\ncall echo(message=\"hello from the harness\"):\n  → {Clip(await echo.InvokeAsync(new JsonObject { ["message"] = "hello from the harness" }), 120)}");

var sum = src.Tools.FirstOrDefault(t => t.Name is "get-sum" or "add");
if (sum is not null)
    Console.WriteLine($"\ncall {sum.Name}(a=2, b=3):\n  → {Clip(await sum.InvokeAsync(new JsonObject { ["a"] = 2, ["b"] = 3 }), 120)}");

Console.WriteLine("\nMCP transport OK — these tools can now be handed to any agent's ToolLoop (gated by default).");

static string Clip(string s, int n = 62) { s = s.Replace('\n', ' ').Trim(); return s.Length <= n ? s : s[..n] + "…"; }
