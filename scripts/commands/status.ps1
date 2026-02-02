# ~/scripts/commands/status.ps1

[CmdletBinding(PositionalBinding = $false)]
param(
  [switch] $Help,
  [switch] $Json,
  [string] $Url = "http://127.0.0.1:8484/v1/status"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Help) {
@"
Usage:
  aether status [--json] [--url <statusUrl>]

Defaults:
  --url http://127.0.0.1:8484/v1/status

Behavior:
  - Calls Core /v1/status and prints the result.
  - Pretty prints JSON unless --json is provided.
"@ | Write-Host
  exit 0
}

try {
  $resp = Invoke-RestMethod -NoProxy -TimeoutSec 5 -Method Get -Uri $Url
  if ($Json) {
    $resp | ConvertTo-Json -Compress -Depth 50 | Write-Output
  } else {
    $resp | ConvertTo-Json -Depth 50 | Write-Host
  }
  exit 0
}
catch {
  Write-Host "Core status request failed: $Url" -ForegroundColor Red
  Write-Host ("{0}: {1}" -f $_.Exception.GetType().Name, $_.Exception.Message)
  exit 1
}
