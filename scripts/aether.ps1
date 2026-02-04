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

function Remove-Ansi([string] $s) {
  return ($s -replace "`e\[[0-?]*[ -/]*[@-~]", "")
}

function Get-CommandMap {
  param([string] $CommandsDir)

  $map = @{}
  if (-not (Test-Path -LiteralPath $CommandsDir)) { return $map }

  Get-ChildItem -LiteralPath $CommandsDir -Filter "*.ps1" -File | ForEach-Object {
    $name = [IO.Path]::GetFileNameWithoutExtension($_.Name).ToLowerInvariant()
    $map[$name] = $_.FullName
  }

  return $map
}

function Get-PowerShellExe {
  if (Get-Command pwsh -ErrorAction SilentlyContinue) { return "pwsh" }
  return "powershell"
}

function Try-ReadCommentHelpSynopsis {
  param([string] $Path)

  try {
    $head = @(Get-Content -LiteralPath $Path -TotalCount 250 -ErrorAction Stop)
  } catch {
    return $null
  }

  if ($head.Length -eq 0) { return $null }

  $start = $null
  $end   = $null

  for ($i = 0; $i -lt $head.Length; $i++) {
    if ($head[$i] -match '^\s*<#') { $start = $i; break }
  }
  if ($null -eq $start) { return $null }

  for ($j = $start + 1; $j -lt $head.Length; $j++) {
    if ($head[$j] -match '#>\s*$') { $end = $j; break }
  }
  if ($null -eq $end) { return $null }

  $block = @($head[$start..$end])

  $synLine = $null
  for ($k = 0; $k -lt $block.Length; $k++) {
    if ($block[$k] -match '^\s*\.SYNOPSIS\s*$') { $synLine = $k; break }
  }
  if ($null -eq $synLine) { return $null }

  $lines = New-Object System.Collections.Generic.List[string]
  for ($m = $synLine + 1; $m -lt $block.Length; $m++) {
    $ln = $block[$m]
    if ($ln -match '^\s*\.\w+') { break }
    $t = ($ln -replace '^\s+','').TrimEnd()
    if ($t) { $lines.Add($t) }
  }

  if ($lines.Count -eq 0) { return $null }
  return ($lines -join " ").Trim()
}

function Try-GetUsageFromHelpSwitchChild {
  param([string] $Path)

  $exe = Get-PowerShellExe
  try {
    $raw = & $exe -NoProfile -ExecutionPolicy Bypass -File $Path -Help 2>&1 | Out-String
  } catch {
    return $null
  }

  $raw = Remove-Ansi ([string]$raw)
  $lines = @($raw -split "\r?\n" | ForEach-Object { $_.TrimEnd() })

  $usageMatch = ($lines | Select-String -Pattern '^\s*Usage\s*:' | Select-Object -First 1)
  if ($usageMatch) {
    $usageIdx = $usageMatch.LineNumber # 1-based
    $start = [Math]::Max(0, $usageIdx - 1)
    $snippet = @()

    for ($i = $start; $i -lt $lines.Length; $i++) {
      $t = $lines[$i].Trim()
      if (-not $t -and $snippet.Length -gt 0) { break }
      if ($t) { $snippet += $t }
      if ($snippet.Length -ge 12) { break }
    }

    if ($snippet.Length -gt 0) { return ($snippet -join "`n") }
  }

  $snip = @($lines | Where-Object { $_.Trim() } | Select-Object -First 8)
  if ($snip.Length -gt 0) { return ($snip -join "`n") }
  return $null
}

function Get-CommandMeta {
  param(
    [string] $Name,
    [string] $Path
  )

  $syn = Try-ReadCommentHelpSynopsis -Path $Path
  if (-not $syn) { $syn = "<no synopsis>" }

  $usage = Try-GetUsageFromHelpSwitchChild -Path $Path
  if (-not $usage) { $usage = "<no usage (add -Help)>" }

  [pscustomobject]@{
    Name = $Name
    Synopsis = $syn
    Usage = $usage
  }
}

# ------------------------- help (dispatcher-owned) -------------------------

function Show-Usage {
  param(
    [hashtable] $CommandMap,
    [string] $CommandsDir
  )

  $items = New-Object System.Collections.Generic.List[object]
  $items.Add([pscustomobject]@{
    Name = "help"
    Synopsis = "List available commands and show per-command usage."
    Usage = "Usage:`n  aether help`n  aether help <command>"
  })

  foreach ($k in (@($CommandMap.Keys) | Sort-Object)) {
    $items.Add((Get-CommandMeta -Name $k -Path $CommandMap[$k]))
  }

  Write-Host @"
Usage:
  aether <command> [args...]

Commands directory:
  $CommandsDir

Available commands:
"@

  foreach ($it in $items) {
    Write-Host ("  {0,-12} {1}" -f $it.Name, $it.Synopsis)
  }

  Write-Host ""
  Write-Host "Per-command usage:"
  foreach ($it in $items) {
    Write-Host ""
    Write-Host ("[{0}]" -f $it.Name)
    Write-Host $it.Usage
  }
}

function Show-CommandHelp {
  param(
    [string] $Command,
    [hashtable] $CommandMap,
    [string] $CommandsDir
  )

  $cmd = $Command.ToLowerInvariant()

  if ($cmd -eq "help") {
    Write-Host "Usage:`n  aether help`n  aether help <command>"
    exit 0
  }

  if (-not $CommandMap.ContainsKey($cmd)) {
    Write-Host "Unknown command: $Command`n"
    Show-Usage -CommandMap $CommandMap -CommandsDir $CommandsDir
    exit 2
  }

  $path = $CommandMap[$cmd]

  $usage = Try-GetUsageFromHelpSwitchChild -Path $path
  if ($usage) {
    Write-Host $usage
    exit 0
  }

  $syn = Try-ReadCommentHelpSynopsis -Path $path
  if ($syn) {
    Write-Host ("{0}: {1}" -f $cmd, $syn)
    exit 0
  }

  Write-Host "No help available for '$cmd'. Add a -Help switch to: $path"
  exit 1
}

# ------------------------- Dispatch -------------------------

$commandsDir = Join-Path $PSScriptRoot "commands"
$cmdMap      = Get-CommandMap -CommandsDir $commandsDir

$argv = @($Args)
$sub  = if ($argv.Length -gt 0) { $argv[0].ToLowerInvariant() } else { "help" }
$rest = if ($argv.Length -gt 1) { @($argv[1..($argv.Length-1)]) } else { @() }

if ($sub -in @("-h","--help")) { $sub = "help"; $rest = @() }

switch ($sub) {
  "help" {
    if ($rest.Length -ge 1) {
      Show-CommandHelp -Command $rest[0] -CommandMap $cmdMap -CommandsDir $commandsDir
    }
    Show-Usage -CommandMap $cmdMap -CommandsDir $commandsDir
    exit 0
  }

  default {
    if ($cmdMap.ContainsKey($sub)) {
      & $cmdMap[$sub] @rest
      exit (Get-ExitCode)
    }

    Write-Host "Unknown command: $sub`n"
    Show-Usage -CommandMap $cmdMap -CommandsDir $commandsDir
    exit 2
  }
}
