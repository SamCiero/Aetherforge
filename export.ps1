# ~/scripts/commands/export.ps1

[CmdletBinding(PositionalBinding = $false)]
param(
  [switch] $Help,
  [Parameter(Mandatory=$false)]
  [int] $Id,
  [string] $BaseUrl = "http://127.0.0.1:8484"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Help -or $Id -le 0) {
@"
Usage:
  aether export <id> [--baseUrl http://127.0.0.1:8484]

Calls:
  POST /v1/export/{id}
"@ | Write-Host
  if ($Help) { exit 0 } else { exit 2 }
}

$uri = "$BaseUrl/v1/export/$Id"
try {
  $resp = Invoke-RestMethod -NoProxy -TimeoutSec 30 -Method Post -Uri $uri
  $resp | ConvertTo-Json -Depth 20 | Write-Host
  exit 0
}
catch {
  Write-Host "Export failed: $uri" -ForegroundColor Red
  Write-Host ("{0}: {1}" -f $_.Exception.GetType().Name, $_.Exception.Message)
  exit 1
}
