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
  aether dev-core [args...]

Starts the core dev runner (WSL core).
Pass-through args are forwarded to dev-core.ps1.
"@ | Write-Host
  exit 0
}

$root = $PSScriptRoot
$bin  = Split-Path $root -Parent
& (Join-Path $bin "dev-core.ps1") @Args
