# D:/Aetherforge/scripts/commands/export.ps1

<#
.SYNOPSIS
  Trigger an export for a conversation ID via the Core API and print the JSON response.
#>

[CmdletBinding(PositionalBinding = $false)]
param(
  [switch] $Help,

  [Parameter(Mandatory = $false)]
  [int] $Id,

  [string] $BaseUrl = "http://127.0.0.1:8484"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Show-Usage {
@"
Usage:
  aether export -Id <id> [-BaseUrl http://127.0.0.1:8484]

Calls:
  POST /v1/export/{id}

Notes:
  - Prefer 127.0.0.1 over localhost for PowerShell web cmdlets.
"@ | Write-Host
}

if ($Help) {
  Show-Usage
  exit 0
}

if ($Id -le 0) {
  Show-Usage
  exit 2
}

$base = $BaseUrl.TrimEnd("/")
$uri = "$base/v1/export/$Id"

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
