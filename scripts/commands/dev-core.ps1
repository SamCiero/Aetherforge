# D:/Aetherforge/scripts/commands/dev-core.ps1

<#
.SYNOPSIS
  Start the Aetherforge.Core service in WSL using dotnet (optionally build and/or watch).
#>

[CmdletBinding(PositionalBinding = $false)]
param(
  [switch] $Help,

  # Common toggles (optional)
  [switch] $NoBuild,
  [switch] $Watch,

  [Parameter(ValueFromRemainingArguments = $true)]
  [string[]] $Args
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Help) {
@"
Usage:
  aether dev-core [-NoBuild] [-Watch] [-- <extra dotnet args...>]

Description:
  Starts the Aetherforge.Core service in WSL (Ubuntu) using dotnet.
  This command is the implementation (not a forwarder).

Flags:
  -NoBuild   Skip 'dotnet build' before run.
  -Watch     Use 'dotnet watch run' instead of 'dotnet run'.
  --         Everything after this is passed to dotnet.

Examples:
  aether dev-core
  aether dev-core -Watch
  aether dev-core -- --urls http://127.0.0.1:8484
"@ | Write-Host
  exit 0
}

function Get-RepoRoot {
  try {
    $p = (git -C $PSScriptRoot rev-parse --show-toplevel 2>$null).Trim()
    if ($p) { return $p }
  } catch {}
  throw "Could not determine repo root (git rev-parse failed)."
}

$repo = Get-RepoRoot

# Build/run ONLY the Core project in WSL (avoid Windows-targeting projects in the .sln)
$coreProjectRel = "src/Aetherforge.Core/Aetherforge.Core.csproj"
$coreProjectWin = Join-Path $repo $coreProjectRel
if (-not (Test-Path -LiteralPath $coreProjectWin)) {
  throw "Core project not found: $coreProjectWin"
}

# We run inside WSL, so convert Windows path to /mnt/... automatically.
# wslpath -a handles D:\Aetherforge -> /mnt/d/Aetherforge
$repoWsl = (wsl.exe -- wslpath -a $repo) -join "`n"
$repoWsl = $repoWsl.Trim()

if (-not $repoWsl) { throw "Failed to convert repo path to WSL path." }

# Escape for bash single-quoted literal
$repoWslEsc = ($repoWsl -replace "'", "'\''")

# Base command (structured to avoid watch/run option parsing edge cases)
$dotnetCmd = if ($Watch) {
  "watch --project ./$coreProjectRel run"
} else {
  "run --project ./$coreProjectRel"
}

# Extra args pass-through: allow user to pass dotnet args after '--'
$extra = @($Args)

# Build first unless skipped (build ONLY Core project, not the solution)
if (-not $NoBuild) {
  Write-Host "Building (WSL): dotnet build ./$coreProjectRel" -ForegroundColor Cyan
  wsl.exe -- bash -lc "cd '$repoWslEsc' && dotnet build ./$coreProjectRel"
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Write-Host "Starting Core (WSL): dotnet $dotnetCmd" -ForegroundColor Cyan

# Quote pass-through args safely for bash.
$extraJoined = ""
if ($extra.Count -gt 0) {
  $escaped = $extra | ForEach-Object { "'" + ($_ -replace "'", "'\''") + "'" }
  $extraJoined = " " + ($escaped -join " ")
}

$bash = "cd '$repoWslEsc' && dotnet $dotnetCmd$extraJoined"

wsl.exe -- bash -lc $bash
exit $LASTEXITCODE
