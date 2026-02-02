# ~/scripts/commands/dev-core.ps1

# scripts/commands/dev-core.ps1

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
  aether dev-core [--no-build] [--watch] [-- <extra dotnet args...>]

Description:
  Starts the Aetherforge.Core service in WSL (Ubuntu) using dotnet.
  This command is the implementation (not a forwarder).

Flags:
  --no-build   Skip 'dotnet build' before run.
  --watch      Use 'dotnet watch run' instead of 'dotnet run'.
  --           Everything after this is passed to dotnet.

Examples:
  aether dev-core
  aether dev-core --watch
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

# We run inside WSL, so convert Windows path to /mnt/... automatically.
# wslpath -a handles D:\Aetherforge -> /mnt/d/Aetherforge
$repoWsl = (wsl.exe -- wslpath -a $repo) -join "`n"
$repoWsl = $repoWsl.Trim()

if (-not $repoWsl) { throw "Failed to convert repo path to WSL path." }

# Base command
$dotnetVerb = if ($Watch) { "watch run" } else { "run" }

# Extra args pass-through: allow user to pass dotnet args after '--'
$extra = @($Args)

# Build first unless skipped
if (-not $NoBuild) {
  Write-Host "Building (WSL): dotnet build .\Aetherforge.sln" -ForegroundColor Cyan
  wsl.exe -- bash -lc "cd '$repoWsl' && dotnet build ./Aetherforge.sln"
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Write-Host "Starting Core (WSL): dotnet $dotnetVerb --project ./src/Aetherforge.Core" -ForegroundColor Cyan

# Build the final bash command
# Note: join extra args safely; we treat them as already-formed dotnet args.
$extraJoined = ""
if ($extra.Count -gt 0) {
  # Escape single quotes for bash literal strings
  $escaped = $extra | ForEach-Object { $_.Replace("'", "''") }
  $extraJoined = " " + ($escaped -join " ")
}

$bash = "cd '$repoWsl' && dotnet $dotnetVerb --project ./src/Aetherforge.Core$extraJoined"

wsl.exe -- bash -lc $bash
exit $LASTEXITCODE
