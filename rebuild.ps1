# ~/scripts/commands/rebuild.ps1

[CmdletBinding(PositionalBinding = $false)]
param([switch] $Help)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Help) {
@"
Usage:
  aether rebuild

Runs:
  dotnet restore
  dotnet build
"@ | Write-Host
  exit 0
}

$repo = (git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
Push-Location $repo
try {
  dotnet restore .\Aetherforge.sln
  dotnet build .\Aetherforge.sln
}
finally { Pop-Location }
