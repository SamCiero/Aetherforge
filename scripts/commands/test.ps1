# D:/Aetherforge/scripts/commands/test.ps1

<#
.SYNOPSIS
  Run the solution test suite with dotnet test.
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
  aether test

Runs:
  dotnet test .\Aetherforge.sln
"@ | Write-Host
  exit 0
}

$repo = Get-RepoRoot

Push-Location $repo
try {
  Invoke-Native dotnet test .\Aetherforge.sln
  exit 0
}
finally {
  Pop-Location
}
