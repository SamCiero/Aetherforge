# ~/scripts/commands/test.ps1

[CmdletBinding(PositionalBinding = $false)]
param([switch] $Help)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Help) {
@"
Usage:
  aether test

Runs:
  dotnet test
"@ | Write-Host
  exit 0
}

$repo = (git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
Push-Location $repo
try {
  dotnet test .\Aetherforge.sln
}
finally { Pop-Location }
