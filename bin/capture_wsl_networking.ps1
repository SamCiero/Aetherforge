# capture_wsl_networking.ps1
# Produces: D:\Aetherforge\logs\bootstrap\wsl_networking.txt
# Works in PowerShell 7.x and Windows PowerShell 5.1
# Adds breadcrumbs + timeouts; never hangs indefinitely.

[CmdletBinding()]
param(
  [string]$AetherRoot = "D:\Aetherforge",
  [string]$Distro = "",                 # optional override; if empty, auto-detect default (*)
  [int]$WslTimeoutSec = 20,
  [int]$IpTimeoutSec = 15
)

$ErrorActionPreference = "Stop"

$OutDir  = Join-Path $AetherRoot "logs\bootstrap"
$OutFile = Join-Path $OutDir "wsl_networking.txt"
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Remove-Item -Force -ErrorAction SilentlyContinue $OutFile

function Append([string]$s) {
  $s | Out-File -FilePath $OutFile -Append -Encoding utf8
}

function Step([string]$name) {
  $line = "=== step: $name ==="
  Write-Host $line
  Append $line
}

function Capture-NativeTimeout {
  param(
    [Parameter(Mandatory=$true)][string]$Exe,
    [Parameter(Mandatory=$true)][string[]]$Args,
    [Parameter(Mandatory=$true)][int]$TimeoutSec,
    [ref]$TimedOut
  )

  $TimedOut.Value = $false

  $tmpOut = Join-Path $env:TEMP ("aether_stdout_" + [guid]::NewGuid().ToString("N") + ".txt")
  $tmpErr = Join-Path $env:TEMP ("aether_stderr_" + [guid]::NewGuid().ToString("N") + ".txt")

  try {
    $p = Start-Process -FilePath $Exe -ArgumentList $Args -NoNewWindow -PassThru `
      -RedirectStandardOutput $tmpOut -RedirectStandardError $tmpErr

    if (-not $p.WaitForExit($TimeoutSec * 1000)) {
      $TimedOut.Value = $true
      try { $p.Kill() } catch {}
    }

    $lines = @()
    if (Test-Path $tmpOut) { $lines += Get-Content $tmpOut }
    if (Test-Path $tmpErr) { $lines += Get-Content $tmpErr }
    return ,$lines
  }
  finally {
    Remove-Item -Force -ErrorAction SilentlyContinue $tmpOut
    Remove-Item -Force -ErrorAction SilentlyContinue $tmpErr
  }
}

function Run-NativeTimeout {
  param(
    [Parameter(Mandatory=$true)][string]$Exe,
    [Parameter(Mandatory=$true)][string[]]$Args,
    [Parameter(Mandatory=$true)][int]$TimeoutSec
  )

  $to = $false
  $lines = Capture-NativeTimeout -Exe $Exe -Args $Args -TimeoutSec $TimeoutSec -TimedOut ([ref]$to)

  if ($to) {
    Append ("<timeout> " + $Exe + " " + ($Args -join " "))
  }

  if ($lines -and $lines.Count -gt 0) {
    $lines | Out-File -FilePath $OutFile -Append -Encoding utf8
  }
}

function Detect-DefaultDistroName {
  $to = $false
  $lines = Capture-NativeTimeout -Exe "wsl" -Args @("-l","-v") -TimeoutSec 5 -TimedOut ([ref]$to)

  if ($to -or -not $lines) { return $null }

  foreach ($ln in $lines) {
    $t = ($ln -replace "`0","").Trim()
    # Matches: "* Ubuntu           Running         2"
    if ($t -match '^\*\s+(.+?)\s{2,}(Running|Stopped)\s+\d+\s*$') {
      return $Matches[1].Trim()
    }
  }
  return $null
}

# ---- Begin capture ----

Step "captured_utc"
Append "=== captured_utc ==="
Append ((Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"))
Append ""

Step "powershell_host"
Append "=== powershell host ==="
Append ("ps_edition: " + $PSVersionTable.PSEdition)
Append ("ps_version: " + $PSVersionTable.PSVersion.ToString())
Append ""

$WslCfg = Join-Path $env:USERPROFILE ".wslconfig"
Step "wslconfig"
Append "=== .wslconfig (%USERPROFILE%\.wslconfig) ==="
if (Test-Path $WslCfg) { Get-Content $WslCfg | Out-File $OutFile -Append -Encoding utf8 }
else { Append "<missing .wslconfig>" }
Append ""

Step "wsl_version"
Append "=== wsl --version ==="
Run-NativeTimeout -Exe "wsl" -Args @("--version") -TimeoutSec $WslTimeoutSec
Append ""

Step "wsl_status"
Append "=== wsl --status ==="
Run-NativeTimeout -Exe "wsl" -Args @("--status") -TimeoutSec $WslTimeoutSec
Append ""

Step "wsl_list"
Append "=== wsl -l -v ==="
Run-NativeTimeout -Exe "wsl" -Args @("-l","-v") -TimeoutSec $WslTimeoutSec
Append ""

Step "windows_ip_summary"
Append "=== Windows IP summary ==="
$job = Start-Job -ScriptBlock {
  Get-NetIPAddress -AddressFamily IPv4 |
    Sort-Object InterfaceAlias,IPAddress |
    Format-Table -AutoSize |
    Out-String
}
if (Wait-Job $job -Timeout $IpTimeoutSec) {
  (Receive-Job $job) | Out-File -FilePath $OutFile -Append -Encoding utf8
} else {
  Append "<timeout> Get-NetIPAddress"
}
Remove-Job -Force -ErrorAction SilentlyContinue $job
Append ""

Step "resolve_distro"
$ResolvedDistro = $null
if (-not [string]::IsNullOrWhiteSpace($Distro)) {
  $ResolvedDistro = $Distro.Trim()
} else {
  $ResolvedDistro = Detect-DefaultDistroName
}
if ($ResolvedDistro) { Append ("resolved_distro: " + $ResolvedDistro) }
else { Append "resolved_distro: <default>" }
Append ""

# Single-line WSL command to avoid argument boundary / here-string issues
$Cmd = "echo '--- os-release ---'; (cat /etc/os-release 2>/dev/null || true); " +
       "echo '--- ip -4 addr ---'; ip -4 addr; " +
       "echo '--- ip route ---'; ip route; " +
       "echo '--- resolv.conf ---'; (cat /etc/resolv.conf 2>/dev/null || true)"

Step "wsl_network_snapshot"
if ($ResolvedDistro) {
  Append ("=== WSL (" + $ResolvedDistro + ") network snapshot ===")
  Run-NativeTimeout -Exe "wsl" -Args @("-d",$ResolvedDistro,"--","bash","--noprofile","--norc","-c",$Cmd) -TimeoutSec $WslTimeoutSec
} else {
  Append "=== WSL (default) network snapshot ==="
  Run-NativeTimeout -Exe "wsl" -Args @("--","bash","-c",$Cmd) -TimeoutSec $WslTimeoutSec
}
Append ""

Step "check_mirrored"
Append "=== CHECK: networkingMode=mirrored present? ==="
$mirrored = $false
if (Test-Path $WslCfg) {
  $t = Get-Content $WslCfg -Raw
  $mirrored = ($t -match 'networkingMode\s*=\s*mirrored')
}
$mirroredTxt = if ($mirrored) { "YES" } else { "NO" }
Append ("networkingMode=mirrored: " + $mirroredTxt)
