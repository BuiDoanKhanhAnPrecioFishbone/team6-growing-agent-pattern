# run-live.ps1 - the LIVE measurement pass on an Azure AI Foundry model.
# Turns the "mechanism / illustrated" rows of the evidence pack into MEASURED numbers.
# The deterministic proofs already pass offline (scripts/run-evidence.ps1); this needs a real model.
#
# Prereq - set your Foundry deployment first (see docs/FOUNDRY-SETUP.md):
#   $env:AGENT_LLM_BASE_URL     = "https://<resource>.openai.azure.com/openai/v1"
#   $env:AGENT_LLM_API_KEY      = "<key>"
#   $env:AGENT_LLM_AUTH         = "api-key"
#   $env:AGENT_LLM_MODEL        = "gpt-4.1-mini"
#   $env:AGENT_LLM_MODEL_STRONG = "gpt-5.1"    # optional, enables the escalation rows
#
#   powershell -File scripts\run-live.ps1      (or: pwsh scripts/run-live.ps1)
$ErrorActionPreference = "Continue"
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root

if (-not $env:AGENT_LLM_BASE_URL -or -not $env:AGENT_LLM_MODEL) {
  Write-Host "AGENT_LLM_* not set - see the header of this script (docs/FOUNDRY-SETUP.md)." -ForegroundColor Yellow
  Pop-Location
  exit 1
}
$strong = "(unset)"
if ($env:AGENT_LLM_MODEL_STRONG) { $strong = $env:AGENT_LLM_MODEL_STRONG }
Write-Host ("model = {0}   strong = {1}" -f $env:AGENT_LLM_MODEL, $strong) -ForegroundColor Cyan
Write-Host ""

Write-Host "1/3  per-lever ablation  (memory / best-of-N / self-verify / escalation)" -ForegroundColor Cyan
dotnet run --project ablate

Write-Host ""
Write-Host "2/3  cost thesis  (cheap+harness vs frontier: quality and real cost)" -ForegroundColor Cyan
dotnet run --project costbench

Write-Host ""
Write-Host "3/3  escalation benchmark  (frontier quality at a fraction of the cost)" -ForegroundColor Cyan
dotnet run --project escbench

Write-Host ""
Write-Host "-- optional: a REAL bake (makes the compounding post-bake column MEASURED) --" -ForegroundColor Cyan
Write-Host "  1. dotnet run --project slowloop              # writes sft.jsonl (the fine-tune input)"
Write-Host "  2. submit that sft.jsonl as an Azure AI Foundry fine-tune job"
Write-Host "  3. set AGENT_LLM_MODEL to your tuned deployment"
Write-Host "  4. dotnet run --project slowloop              # graduation now measures the baked model"

Pop-Location
