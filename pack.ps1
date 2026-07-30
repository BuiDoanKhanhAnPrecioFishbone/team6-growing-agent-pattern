# Packs the shared Growing-Agent harness libraries to ./artifacts as NuGet packages,
# so another repo can consume the harness via a feed. See docs/START-A-NEW-REPO.md.
$out = "artifacts"
dotnet pack shared/AIAssistant.AgentHarness/AIAssistant.AgentHarness.csproj             -c Release -o $out
dotnet pack shared/AIAssistant.AgentHarness.Cosmos/AIAssistant.AgentHarness.Cosmos.csproj -c Release -o $out
dotnet pack shared/AIAssistant.AgentHost/AIAssistant.AgentHost.csproj                    -c Release -o $out
Write-Host ""
Write-Host "Packed to ./$out. Publish, e.g.:"
Write-Host "  GitHub Packages: dotnet nuget push `"$out/*.nupkg`" --source github --api-key <PAT>"
Write-Host "  local feed:      (add ./$out as a nuget source in the new repo)"
