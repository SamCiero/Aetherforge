[CmdletBinding(PositionalBinding = $false)]
param(
  [switch] $Help,
  [Parameter(ValueFromRemainingArguments = $true)]
  [string[]] $Args
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Help) {
@"
Usage:
  aether start [args...]

Alias for: aether dev-core
"@ | Write-Host
  exit 0
}

$root = $PSScriptRoot
$bin  = Split-Path $root -Parent
& (Join-Path $bin "dev-core.ps1") @Args
