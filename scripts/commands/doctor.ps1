# D:/Aetherforge/scripts/commands/doctor.ps1

<#
.SYNOPSIS
  Print environment and tooling diagnostics for debugging build/restore and Visual Studio issues.
#>

[CmdletBinding(PositionalBinding = $false)]
param(
  [switch] $Help
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-RepoRoot {
  try {
    $p = (git -C $PSScriptRoot rev-parse --show-toplevel 2>$null).Trim()
    if ($p) { return $p }
  } catch {}
  throw "Could not determine repo root (git rev-parse failed)."
}

function Invoke-Native {
  param(
    [Parameter(Mandatory = $true)] [string] $Exe,
    [Parameter(ValueFromRemainingArguments = $true)] [string[]] $Argv
  )

  & $Exe @Argv
  $code = $LASTEXITCODE
  if ($code -ne 0) { exit $code }
}

if ($Help) {
@"
Usage:
  aether doctor

Description:
  Prints environment + tooling diagnostics useful for debugging restores/build/VS issues.
"@ | Write-Host
  exit 0
}

$repo = Get-RepoRoot

Write-Host "Repo: $repo"
Write-Host ""

Write-Host "dotnet:"
Invoke-Native dotnet --info
Write-Host ""

Write-Host "NuGet global packages folder (dotnet):"
Invoke-Native dotnet nuget locals global-packages -l
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
$envRows =
  Get-ChildItem Env: |
  Where-Object { $_.Name -like "DOTNET_*" -or $_.Name -like "NUGET_*" } |
  Sort-Object Name |
  Format-Table -AutoSize | Out-String

Write-Host $envRows.TrimEnd()
exit 0
