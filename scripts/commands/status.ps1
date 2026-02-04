# D:/Aetherforge/scripts/commands/status.ps1

<#
.SYNOPSIS
  Fetch Core /v1/status and print the result (pretty or compact JSON).
#>

[CmdletBinding(PositionalBinding = $false)]
param(
  [switch] $Help,
  [switch] $Json,
  [string] $Url = "http://127.0.0.1:8484/v1/status"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Show-Usage {
@"
Usage:
  aether status [-Json] [-Url <statusUrl>]

Defaults:
  -Url http://127.0.0.1:8484/v1/status

Behavior:
  - Calls Core /v1/status and prints the result.
  - Pretty prints JSON unless -Json is provided (compact JSON).
"@ | Write-Host
}

if ($Help) {
  Show-Usage
  exit 0
}

$u = $Url.Trim()
if (-not $u) { throw "Url is empty." }

try {
  $resp = Invoke-RestMethod -NoProxy -TimeoutSec 5 -Method Get -Uri $u

  if ($Json) {
    $resp | ConvertTo-Json -Compress -Depth 50 | Write-Output
  } else {
    $resp | ConvertTo-Json -Depth 50 | Write-Output
  }

  exit 0
}
catch {
  Write-Host "Core status request failed: $u" -ForegroundColor Red
  Write-Host ("{0}: {1}" -f $_.Exception.GetType().Name, $_.Exception.Message)
  exit 1
}
