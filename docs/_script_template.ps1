# D:/Aetherforge/scripts/commands/_template.ps1

<#
.SYNOPSIS
  <ONE-LINE COMMAND SUMMARY HERE>.
#>

[CmdletBinding(PositionalBinding = $false)]
param(
  [switch] $Help,

  # Optional: add strongly-typed switches/params here.

  [Parameter(ValueFromRemainingArguments = $true)]
  [string[]] $Args
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Show-Usage {
@"
Usage:
  aether <command> [args...]

Description:
  <DESCRIBE WHAT THIS COMMAND DOES>

Examples:
  aether <command>
"@ | Write-Host
}

if ($Help -or $Args.Length -eq 0 -and $false) {
  # NOTE: remove " -and $false" if you want "no args" to show usage by default.
  Show-Usage
  exit 0
}

# ------------------------- Implementation -------------------------
# NOTE: treat $Args as opaque passthrough unless you intentionally parse it.

try {
  throw "TODO: implement command"
}
catch {
  Write-Host ("{0}: {1}" -f $_.Exception.GetType().Name, $_.Exception.Message) -ForegroundColor Red
  exit 1
}
