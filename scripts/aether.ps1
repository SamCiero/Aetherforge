# D:/Aetherforge/scripts/aether.ps1

<#
.SYNOPSIS
  Dispatcher for scripts/commands/*.ps1 (aether <command> [args...]).
#>

[CmdletBinding(PositionalBinding = $false)]
param(
  [Parameter(ValueFromRemainingArguments = $true)]
  [string[]] $CliArgs
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ------------------------- Helpers -------------------------

function Get-ExitCode {
  if (Test-Path variable:LASTEXITCODE) { return $LASTEXITCODE }
  return (if ($?) { 0 } else { 1 })
}

function Get-PowerShellExe {
  if (Get-Command pwsh -ErrorAction SilentlyContinue) { return "pwsh" }
  return "powershell"
}

function Get-CommandMap {
  param([Parameter(Mandatory = $true)][string] $CommandsDir)

  $map = @{}
  if (-not (Test-Path -LiteralPath $CommandsDir)) { return $map }

  Get-ChildItem -LiteralPath $CommandsDir -Filter "*.ps1" -File | ForEach-Object {
    $name = [IO.Path]::GetFileNameWithoutExtension($_.Name).ToLowerInvariant()
    $map[$name] = $_.FullName
  }

  return $map
}

function Try-ReadCommentHelpSynopsis {
  param([Parameter(Mandatory = $true)][string] $Path)

  [string[]] $head = @()
  try {
    $head = @(Get-Content -LiteralPath $Path -TotalCount 250 -ErrorAction Stop)
  } catch {
    return $null
  }

  if (-not $head -or $head.Length -eq 0) { return $null }

  $start = -1
  $end   = -1

  for ($i = 0; $i -lt $head.Length; $i++) {
    if ($head[$i] -match '^\s*<#') { $start = $i; break }
  }
  if ($start -lt 0) { return $null }

  for ($j = $start + 1; $j -lt $head.Length; $j++) {
    if ($head[$j] -match '#>\s*$') { $end = $j; break }
  }
  if ($end -lt 0) { return $null }

  [string[]] $block = @($head[$start..$end])

  $synLine = -1
  for ($k = 0; $k -lt $block.Length; $k++) {
    if ($block[$k] -match '^\s*\.SYNOPSIS\s*$') { $synLine = $k; break }
  }
  if ($synLine -lt 0) { return $null }

  $lines = New-Object System.Collections.Generic.List[string]
  for ($m = $synLine + 1; $m -lt $block.Length; $m++) {
    $ln = $block[$m]
    if ($ln -match '^\s*\.\w+') { break } # next directive
    $t = ($ln -replace '^\s+','').TrimEnd()
    if ($t -eq "#>") { break }
    $t = ($t -replace '\s*#>\s*$','').TrimEnd()
    if ($t) { $lines.Add($t) }
  }

  if ($lines.Count -eq 0) { return $null }
  return ($lines -join " ").Trim()
}

# ------------------------- help (dispatcher-owned) -------------------------

function Show-UsageSummary {
  param(
    [Parameter(Mandatory = $true)][hashtable] $CommandMap,
    [Parameter(Mandatory = $true)][string] $CommandsDir
  )

  Write-Host @"
Usage:
  aether <command> [args...]

Commands directory:
  $CommandsDir

Commands:
"@

  Write-Host ("  {0,-12} {1}" -f "help", "List available commands and show per-command usage.")

  foreach ($k in (@($CommandMap.Keys) | Sort-Object)) {
    $syn = Try-ReadCommentHelpSynopsis -Path $CommandMap[$k]
    if (-not $syn) { $syn = "<no synopsis>" }
    Write-Host ("  {0,-12} {1}" -f $k, $syn)
  }

  Write-Host ""
  Write-Host "Per-command usage:"
  Write-Host "  aether help <command>"
}

function Show-CommandHelp {
  param(
    [Parameter(Mandatory = $true)][string] $Command,
    [Parameter(Mandatory = $true)][hashtable] $CommandMap,
    [Parameter(Mandatory = $true)][string] $CommandsDir
  )

  $cmd = $Command.ToLowerInvariant()

  if ($cmd -eq "help") {
@"
Usage:
  aether help
  aether help <command>
"@ | Write-Host
    exit 0
  }

  if (-not $CommandMap.ContainsKey($cmd)) {
    Write-Host "Unknown command: $Command`n"
    Show-UsageSummary -CommandMap $CommandMap -CommandsDir $CommandsDir
    exit 2
  }

  $path = $CommandMap[$cmd]
  $exe  = Get-PowerShellExe

  # Child process: safe even if the command script calls 'exit'
  & $exe -NoProfile -ExecutionPolicy Bypass -File $path -Help
  exit (Get-ExitCode)
}

# ------------------------- Dispatch -------------------------

$commandsDir = Join-Path $PSScriptRoot "commands"
$cmdMap      = Get-CommandMap -CommandsDir $commandsDir

# Build arg vector deterministically (no if-expression assignments; no scalar collapse)
[string[]] $cli = [string[]]$CliArgs
if ($null -eq $cli) { $cli = @() }

$sub  = "help"
[string[]] $rest = @()

if ($cli.Length -ge 1 -and $null -ne $cli[0] -and $cli[0].ToString().Trim().Length -gt 0) {
  $sub = $cli[0].ToString().ToLowerInvariant()

  if ($cli.Length -gt 1) {
    $rest = [string[]]@($cli[1..($cli.Length - 1)])
  }
}

if ($sub -in @("-h","--help")) {
  $sub = "help"
  $rest = @()
}

switch ($sub) {
  "help" {
    if ($rest.Length -ge 1 -and $null -ne $rest[0] -and $rest[0].ToString().Trim().Length -gt 0) {
      Show-CommandHelp -Command ($rest[0].ToString()) -CommandMap $cmdMap -CommandsDir $commandsDir
    }

    Show-UsageSummary -CommandMap $cmdMap -CommandsDir $commandsDir
    exit 0
  }

  default {
    if ($cmdMap.ContainsKey($sub)) {
      & $cmdMap[$sub] @rest
      exit (Get-ExitCode)
    }

    Write-Host "Unknown command: $sub`n"
    Show-UsageSummary -CommandMap $cmdMap -CommandsDir $commandsDir
    exit 2
  }
}
