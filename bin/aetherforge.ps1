param(
  [ValidateSet("status","dev-core")]
  [string]$Cmd = "status"
)

$root = Split-Path -Parent $PSScriptRoot

switch ($Cmd) {
  "dev-core" {
    & (Join-Path $PSScriptRoot "dev-core.ps1")
  }
  "status" {
    # Canonical URL: 127.0.0.1 (avoid localhost slowness in PS)
    irm "http://127.0.0.1:8484/v1/status" -NoProxy
  }
}
~