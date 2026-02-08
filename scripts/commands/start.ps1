# D:/Aetherforge/scripts/commands/start.ps1

<#
.SYNOPSIS
  Alias for dev-core (start the Core service in WSL using dotnet).
#>

[CmdletBinding(PositionalBinding = $false)]
param(
  [switch] $Help,

  # Forwarded toggles (match dev-core ergonomics)
  [switch] $NoBuild,
  [switch] $Watch,

  [Parameter(ValueFromRemainingArguments = $true)]
  [string[]] $Args
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Show-Usage {
@"
Usage:
  aether start [-NoBuild] [-Watch] [-- <extra dotnet args...>]

Description:
  Temporary alias for 'aether dev-core'. Starts Aetherforge.Core in WSL.
  All additional arguments are forwarded to dev-core.

Flags:
  -NoBuild   Skip 'dotnet build' before run (in WSL).
  -Watch     Use 'dotnet watch run' instead of 'dotnet run'.
  --         Everything after this is passed to dotnet (e.g. app args).

Examples:
  aether start
  aether start -Watch
  aether start -- --urls http://127.0.0.1:8484
"@ | Write-Host
}

if ($Help) {
  Show-Usage
  exit 0
}

$target = Join-Path $PSScriptRoot "dev-core.ps1"
if (-not (Test-Path -LiteralPath $target)) {
  throw "Missing target script: $target"
}

# Build a forward-arg list that preserves switch semantics and passthrough args.
$forward = @()
if ($NoBuild) { $forward += "-NoBuild" }
if ($Watch)   { $forward += "-Watch" }
if ($Args -and $Args.Count -gt 0) { $forward += $Args }

& $target @forward
exit $LASTEXITCODE
