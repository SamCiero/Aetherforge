# ~/scripts/commands/doctor.ps1

[CmdletBinding(PositionalBinding = $false)]
param(
  [switch] $Help
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Help) {
@"
Usage:
  aether doctor

Prints environment + tooling diagnostics useful for debugging restores/build/VS issues.
"@ | Write-Host
  exit 0
}

$repo = (git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
if (-not $repo) { throw "Could not determine repo root." }

Write-Host "Repo: $repo"
Write-Host ""

Write-Host "dotnet:"
dotnet --info
Write-Host ""

Write-Host "NuGet global packages folder (dotnet):"
dotnet nuget locals global-packages -l
Write-Host ""

Write-Host "global.json:"
$gj = Join-Path $repo "global.json"
if (Test-Path $gj) { Get-Content $gj -Raw | Write-Host } else { Write-Host "  (none)" }
Write-Host ""

Write-Host "NuGet.config (repo):"
$nucfg = Join-Path $repo "NuGet.config"
if (Test-Path $nucfg) { Get-Content $nucfg -Raw | Write-Host } else { Write-Host "  (none)" }
Write-Host ""

Write-Host "Environment overrides (DOTNET_*, NUGET_*):"
Get-ChildItem Env: |
  Where-Object { $_.Name -like "DOTNET_*" -or $_.Name -like "NUGET_*" } |
  Sort-Object Name |
  Format-Table -AutoSize
