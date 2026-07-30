# Start a new repo on the Growing-Agent harness

`_template` in *this* repo references the harness by relative path (`..\shared\...`), which only works
inside this repo. To build agents in a **separate repo**, consume the harness the way you'd consume any
library. Two ways — pick by whether you want a shared, versioned dependency (recommended) or a zero-infra copy.

The one package you need is **`AIAssistant.AgentHost`** — it pulls in `AIAssistant.AgentHarness` and
`AIAssistant.AgentHarness.Cosmos` as dependencies, so a single reference gives you the loop, memory, model
client, and one-line HTTP host.

---

## Option A — consume the harness as a NuGet package (recommended)
Clean dependency, and it powers the "update the harness → teammates sync" story: bump the version, teammates
update the package.

### 1. Pack (in this repo)
```powershell
./pack.ps1          # → ./artifacts/*.nupkg  (AgentHarness, .Cosmos, .AgentHost @ 0.1.0)
```

### 2. Publish to a feed (choose one)
```powershell
# GitHub Packages (natural — this repo is on GitHub; needs a PAT with write:packages):
dotnet nuget push "artifacts/*.nupkg" --source "https://nuget.pkg.github.com/<OWNER>/index.json" --api-key <GITHUB_PAT>

# or your Azure DevOps feed:
dotnet nuget push "artifacts/*.nupkg" --source "<your Azure DevOps feed URL>" --api-key az

# or, quickest for local dev — no publish at all: point the new repo at the ./artifacts folder (step 3).
```

### 3. In the NEW repo — a whole agent is three files
`nuget.config` (so it can restore the harness):
```xml
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <!-- pick the one you published to: -->
    <add key="team6" value="https://nuget.pkg.github.com/<OWNER>/index.json" />
    <!-- or a local folder feed: <add key="team6" value="../team6-growing-agent-pattern/artifacts" /> -->
  </packageSources>
  <!-- GitHub Packages needs auth: -->
  <packageSourceCredentials><team6>
    <add key="Username" value="<github-user>" />
    <add key="ClearTextPassword" value="<GITHUB_PAT>" />
  </team6></packageSourceCredentials>
</configuration>
```
`MyAgent.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><LangVersion>latest</LangVersion>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="AIAssistant.AgentHost" Version="0.1.0" />
  </ItemGroup>
</Project>
```
`Program.cs`:
```csharp
await AIAssistant.AgentHost.Host.Run(args, new MyAgent(), port: 5310, blockKey: "myblock");
```
`MyAgent.cs` — implement the three `IAgent` methods (copy the shape from this repo's `_template/TemplateAgent.cs`
or `s2-moat`). Then `dotnet run` → `POST /run`.

### Sync when the harness changes
Bump `<Version>` in the three shared csprojs (e.g. `0.1.0` → `0.2.0`), `./pack.ps1`, push. Teammates:
```powershell
dotnet add package AIAssistant.AgentHost --version 0.2.0
```

---

## Option B — vendor the harness (zero infra, quickest)
Copy the harness source into the new repo. Self-contained, no feed — at the cost of manual updates.

```
cp -r team6-growing-agent-pattern/shared  my-new-repo/shared
```
Then in `MyAgent.csproj` use a project reference instead of a package:
```xml
<ProjectReference Include="..\shared\AIAssistant.AgentHost\AIAssistant.AgentHost.csproj" />
```
Also copy `nuget.config` (for the Cosmos SDK) from this repo. **Sync** by re-copying `shared/`, or wire it
as a `git submodule` / `git subtree` pointing at this repo so `git pull` updates it.

---

## Either way — bring the pattern with you
Copy the skill into the new repo so teammates' coding agents scaffold correctly there too:
```
cp -r team6-growing-agent-pattern/.claude/skills/build-growing-agent  my-new-repo/.claude/skills/
```
And copy `PATTERN.md` (the source of truth) for reference. Now a teammate can open the new repo in Claude Code
and say *"add an agent following our pattern"* — same experience, different repo.

---

## Which to choose
| | NuGet (A) | Vendor (B) |
|--|-----------|------------|
| Setup | publish to a feed once | copy a folder |
| Update/sync | `dotnet add package --version` | re-copy / submodule |
| Best for | multiple repos, a real shared framework | one quick repo, or offline |

For a team building several agent repos, **Option A** is the way — it's what "one common harness, many
repos, everyone on the same version" actually looks like.
