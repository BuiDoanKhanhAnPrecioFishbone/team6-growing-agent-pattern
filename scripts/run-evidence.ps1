# ─────────────────────────────────────────────────────────────────────────────
# run-evidence.ps1 — reproduce the whole evidence pack in one command.
# Runs every DETERMINISTIC proof (offline, no model, no GPU) and tallies pass/fail
# by exit code, then runs the demo/pipeline proofs that emit datasets. The live-only
# proofs (ablate per-lever table, compare quality/cost, a real slowloop bake) need a
# Foundry deployment (AGENT_LLM_*) and are listed at the end.
#
#   pwsh scripts/run-evidence.ps1
# ─────────────────────────────────────────────────────────────────────────────
$ErrorActionPreference = "Continue"
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root

$deterministic = @(
  @{ name = "memory lifecycle  (decay / evict / conflict / dedup)"; proj = "memlife"    },
  @{ name = "consolidation     (memory self-summarizes, 6->2)";     proj = "memcon"     },
  @{ name = "guardrails        (memory poisoning defense, MINJA)";  proj = "guardbench" },
  @{ name = "skill tier        (contrast -> verify -> transfer)";   proj = "skillbench" }
)

$pass = 0; $fail = 0
Write-Host "`nDETERMINISTIC PROOFS (offline, self-verifying)`n" -ForegroundColor Cyan
foreach ($b in $deterministic) {
  dotnet run --project $b.proj *> $null
  if ($LASTEXITCODE -eq 0) { Write-Host ("  [PASS] " + $b.name) -ForegroundColor Green; $pass++ }
  else                     { Write-Host ("  [FAIL] " + $b.name) -ForegroundColor Red;   $fail++ }
}

Write-Host "`nMECHANISM / PIPELINE DEMOS (offline, emit artifacts)`n" -ForegroundColor Cyan
dotnet run --project slowloop *> $null; Write-Host "  [ran ] slowloop  -> compounding.json + sft.jsonl (fast loop -> bake -> graduate)"
dotnet run --project flywheel *> $null; Write-Host "  [ran ] flywheel  -> sft / preference / rl jsonl (training-ready export)"
dotnet run --project orchestrator -- --fresh *> $null; Write-Host "  [ran ] pipeline  -> fast-loop compounding (12 -> 6 iterations)"

Write-Host "`nLIVE-ONLY PROOFS (need AGENT_LLM_* -> a Foundry deployment)`n" -ForegroundColor Cyan
Write-Host "  dotnet run --project ablate     # per-lever ablation table (memory / best-of-N / self-verify / escalation)"
Write-Host "  dotnet run --project costbench   # cheap+harness vs frontier: quality and real cost"
Write-Host "  dotnet run --project escbench    # escalation: frontier quality at a fraction of the cost"

Write-Host ("`nDeterministic proofs: {0} passed, {1} failed.`n" -f $pass, $fail) -ForegroundColor $(if ($fail -eq 0) { "Green" } else { "Red" })
Pop-Location
exit $fail
