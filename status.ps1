[CmdletBinding(PositionalBinding = $false)]
param(
  [switch] $Help
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Help) {
@"
Usage:
  aether status

Runs the Aetherforge status command (via aetherforge.ps1 wrapper).
"@ | Write-Host
  exit 0
}

$root = $PSScriptRoot
$bin  = Split-Path $root -Parent
& (Join-Path $bin "aetherforge.ps1") -Cmd status
