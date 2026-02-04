# D:/Aetherforge/scripts/commands/start.ps1

<#
.SYNOPSIS
  Alias for dev-core (start the Core service in WSL using dotnet).
#>

[CmdletBinding(PositionalBinding = $false)]
param(
  [switch] $Help,

  [Parameter(ValueFromRemainingArguments = $true)]
  [string[]] $Args
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Show-Usage {
@"
Usage:
  aether start [-- <args passed to dev-core...>]

Description:
  Temporary alias for 'aether dev-core'. All arguments are forwarded.

Examples:
  aether start
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

& $target @Args
exit $LASTEXITCODE
