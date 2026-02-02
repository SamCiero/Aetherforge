# ~/scripts/commands/restore.ps1

[CmdletBinding(PositionalBinding = $false)]
param(
  [switch] $Help
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Help) {
@"
Usage:
  aether restore

Clears NuGet caches and deletes build outputs (src/**/bin, src/**/obj, .vs), then restores.
Safe: does NOT touch repo scripts/ directory.
"@ | Write-Host
  exit 0
}

$repo = (git -C $PSScriptRoot rev-parse --show-toplevel).Trim()
if (-not $repo) { throw "Could not determine repo root." }

Push-Location $repo
try {
  Remove-Item -Recurse -Force .\.vs -ErrorAction SilentlyContinue

  Get-ChildItem -Recurse -Directory -Force -Path .\src |
    Where-Object { $_.Name -in @("bin","obj") } |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

  dotnet nuget locals all --clear
  dotnet restore .\Aetherforge.sln
}
finally {
  Pop-Location
}
