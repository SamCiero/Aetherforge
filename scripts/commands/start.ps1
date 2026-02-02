# scripts/commands/start.ps1

[CmdletBinding(PositionalBinding = $false)]
param(
  [switch] $Help,
  [Parameter(ValueFromRemainingArguments = $true)]
  [string[]] $Args
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Help) {
  "Usage: aether start [args...] (temp alias for aether dev-core)" | Write-Host
  exit 0
}

& (Join-Path $PSScriptRoot "dev-core.ps1") @Args
exit $LASTEXITCODE
