# D:/Aetherforge/scripts/aether.ps1

[CmdletBinding(PositionalBinding = $false)]
param(
  [Parameter(ValueFromRemainingArguments = $true)]
  [string[]] $Args
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ------------------------- Helpers -------------------------

function Get-ExitCode {
  if (Test-Path variable:LASTEXITCODE) { return $LASTEXITCODE }
  if ($?) { return 0 } else { return 1 }
}

function Get-PowerShellExe {
  if (Get-Command pwsh -ErrorAction SilentlyContinue) { return "pwsh" }
  return "powershell"
}

function Get-CommandMap {
  param([string] $CommandsDir)

  $map = @{}
  if (-not (Test-Path -LiteralPath $CommandsDir)) { return $map }

  Get-ChildItem -LiteralPath $CommandsDir -Filter "*.ps1" -File |
    ForEach-Object {
      $name = [IO.Path]::GetFileNameWithoutExtension($_.Name).ToLowerInvariant()
      $map[$name] = $_.FullName
    }

  return $map
}

function Try-ReadCommentHelpSynopsis {
  param([string] $Path)

  try {
    $head = @(Get-Content -LiteralPath $Path -TotalCount 250 -ErrorAction Stop)
  } catch {
    return $null
  }

  if (-not $head) { return $null }

  $start = $null
  $end   = $null

  for ($i = 0; $i -lt $head.Count; $i++) {
    if ($head[$i] -match '^\s*<#') { $start = $i; break }
  }
  if ($null -eq $start) { return $null }

  for ($j = $start + 1; $j -lt $head.Count; $j++) {
    if ($head[$j] -match '#>\s*$') { $end = $j; break }
  }
  if ($null -eq $end) { return $null }

  $block = @($head[$start..$end])

  $synLine = $null
  for ($k = 0; $k -lt $block.Count; $k++) {
    if ($block[$k] -match '^\s*\.SYNOPSIS\s*$') { $synLine = $k; break }
  }
  if ($null -eq $synLine) { return $null }

  $lines = New-Object System.Collections.Generic.List[string]
  for ($m = $synLine + 1; $m -lt $block.Count; $m++) {
    $ln = $block[$m]
    if ($ln -match '^\s*\.\w+') { break }
    $t = ($ln -replace '^\s+','').TrimEnd()
    if ($t) { $lines.Add($t) }
  }

  if ($lines.Count -eq 0) { return $null }
  return ($lines -join " ").Trim()
}

# ------------------------- help (dispatcher-owned) -------------------------

function Show-UsageSummary {
  param(
    [hashtable] $CommandMap,
    [string] $CommandsDir
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
    [string] $Command,
    [hashtable] $CommandMap,
    [string] $CommandsDir
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

  # Run in a child process so 'exit' inside command scripts is safe.
  & $exe -NoProfile -ExecutionPolicy Bypass -File $path -Help
  exit (Get-ExitCode)
}

# ------------------------- Dispatch -------------------------

$commandsDir = Join-Path $PSScriptRoot "commands"
$cmdMap      = Get-CommandMap -CommandsDir $commandsDir

$argv = if ($null -eq $Args) { @() } else { @($Args) }

$sub  = if ($argv.Count -gt 0 -and $null -ne $argv[0]) { ($argv[0].ToString()).ToLowerInvariant() } else { "help" }
$rest = if ($argv.Count -gt 1) { @($argv[1..($argv.Count - 1)]) } else { @() }

if ($sub -in @("-h", "--help")) { $sub = "help"; $rest = @() }

switch ($sub) {
  "help" {
    if ($rest.Count -ge 1 -and $null -ne $rest[0]) {
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
