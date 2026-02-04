# D:/Aetherforge/scripts/commands/rebuild.ps1

<#
.SYNOPSIS
  Restore and build the Aetherforge solution with dotnet.
#>

[CmdletBinding(PositionalBinding = $false)]
param([switch] $Help)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-RepoRoot {
  try {
    $p = (git -C $PSScriptRoot rev-parse --show-toplevel 2>$null).Trim()
    if ($p) { return $p }
  } catch {}
  throw "Could not determine repo root (git rev-parse failed)."
}

function Invoke-Native {
  param(
    [Parameter(Mandatory = $true)] [string] $Exe,
    [Parameter(ValueFromRemainingArguments = $true)] [string[]] $Argv
  )

  & $Exe @Argv
  $code = $LASTEXITCODE
  if ($code -ne 0) { exit $code }
}

if ($Help) {
@"
Usage:
  aether rebuild

Runs:
  dotnet restore .\Aetherforge.sln
  dotnet build   .\Aetherforge.sln
"@ | Write-Host
  exit 0
}

$repo = Get-RepoRoot

Push-Location $repo
try {
  Invoke-Native dotnet restore .\Aetherforge.sln
  Invoke-Native dotnet build   .\Aetherforge.sln
  exit 0
}
finally {
  Pop-Location
}
