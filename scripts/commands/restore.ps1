# D:/Aetherforge/scripts/commands/restore.ps1

<#
.SYNOPSIS
  Clear local build outputs and NuGet caches, then restore the solution.
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

function Show-Usage {
@"
Usage:
  aether restore

Description:
  Clears NuGet caches and deletes build outputs (src/**/bin, src/**/obj, .vs), then restores.
  Safe: does NOT touch repo scripts/ directory.
"@ | Write-Host
}

if ($Help) {
  Show-Usage
  exit 0
}

$repo = Get-RepoRoot

Push-Location $repo
try {
  # Delete repo-root .vs (if present)
  if (Test-Path -LiteralPath ".\.vs") {
    Write-Host "Deleting: .vs" -ForegroundColor Cyan
    Remove-Item -Recurse -Force -LiteralPath ".\.vs" -ErrorAction SilentlyContinue
  }

  # Delete bin/obj only under .\src
  if (Test-Path -LiteralPath ".\src") {
    $targets =
      Get-ChildItem -LiteralPath ".\src" -Recurse -Directory -Force -ErrorAction Stop |
      Where-Object { $_.Name -in @("bin", "obj") }

    foreach ($t in $targets) {
      Write-Host ("Deleting: {0}" -f $t.FullName) -ForegroundColor Cyan
      Remove-Item -Recurse -Force -LiteralPath $t.FullName -ErrorAction SilentlyContinue
    }
  }

  Write-Host "Clearing NuGet locals..." -ForegroundColor Cyan
  Invoke-Native dotnet nuget locals all --clear

  Write-Host "Restoring solution..." -ForegroundColor Cyan
  Invoke-Native dotnet restore .\Aetherforge.sln

  exit 0
}
finally {
  Pop-Location
}
