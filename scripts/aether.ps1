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
  return ($s -replace "`e\[[0-9;]*[A-Za-z]", "")
}

function Get-RepoRoot {
  # Works even if invoked from elsewhere
  try {
    $p = (git -C $PSScriptRoot rev-parse --show-toplevel 2>$null).Trim()
    if ($p) { return $p }
  } catch {}
  return (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}

function Get-CommandMap {
  param([string] $CommandsDir)

  $map = @{}
  if (-not (Test-Path $CommandsDir)) { return $map }

  Get-ChildItem -Path $CommandsDir -Filter "*.ps1" -File | ForEach-Object {
    $name = [IO.Path]::GetFileNameWithoutExtension($_.Name).ToLowerInvariant()
    $map[$name] = $_.FullName
  }

  return $map
}

function Show-Usage {
  param(
    [hashtable] $CommandMap,
    [string] $CommandsDir
  )

@"
Usage:
  aether <command> [args...]

Core commands:
  aether status
  aether start
  aether dev-core
  aether ask <prompt...> [-m <model>] [--json]

More commands:
  aether help
  aether help <command>

Notes:
  - Canonical base URLs: http://127.0.0.1:<port> (avoid localhost for PowerShell web cmdlets)
  - Commands directory: $CommandsDir

Available commands:
$(
  ($CommandMap.Keys | Sort-Object | ForEach-Object { "  - $_" }) -join "`n"
)
"@ | Write-Host
}

function Show-CommandHelp {
  param(
    [string] $Command,
    [hashtable] $CommandMap
  )

  $cmd = $Command.ToLowerInvariant()
  if (-not $CommandMap.ContainsKey($cmd)) {
    Write-Host "Unknown command: $Command"
    return
  }

  # Convention: each command script can implement a -Help switch that prints its own usage.
  & $CommandMap[$cmd] -Help
  exit (Get-ExitCode)
}

# ------------------------- ask (inline) -------------------------

function Invoke-OllamaGenerate {
  param(
    [Parameter(Mandatory = $true)] [string] $Prompt,
    [string] $Model = "qwen2.5:7b-instruct",
    [switch] $Json
  )

  $uri = "http://127.0.0.1:11434/api/generate"
  $body = @{ model = $Model; prompt = $Prompt; stream = $false } | ConvertTo-Json -Compress -Depth 10

  try {
    $resp = Invoke-RestMethod -NoProxy -TimeoutSec 120 -Method Post -Uri $uri -ContentType "application/json" -Body $body
    if ($Json) { return ($resp | ConvertTo-Json -Compress -Depth 10) }
    return $resp.response
  }
  catch {
    $env:PROMPT_B64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Prompt))
    $env:MODEL = $Model

    $bash = @'
set -euo pipefail
payload="$(python3 - <<'PY'
import os,base64,json
p=base64.b64decode(os.environ["PROMPT_B64"]).decode("utf-8")
m=os.environ.get("MODEL","qwen2.5:7b-instruct")
print(json.dumps({"model":m,"prompt":p,"stream":False}))
PY
)"
printf '%s' "$payload" | curl -fsS http://127.0.0.1:11434/api/generate -H 'Content-Type: application/json' --data-binary @-
'@
    $bash = $bash.Replace("`r", "")
    $raw = (wsl.exe -- bash -lc $bash) -join "`n"
    $raw = Remove-Ansi $raw

    if ($Json) { return $raw.Trim() }

    try { return ((ConvertFrom-Json $raw).response) }
    catch { return $raw.Trim() }
  }
}

function Invoke-Ask {
  param([string[]] $Rest)

  $rest = @($Rest)
  if ($rest.Count -lt 1) { throw "Usage: aether ask <prompt...> [-m <model>] [--json]" }

  $model  = "qwen2.5:7b-instruct"
  $asJson = $false

  $i = 0
  $promptParts = New-Object System.Collections.Generic.List[string]
  while ($i -lt $rest.Count) {
    $a = $rest[$i]
    if ($a -in @("-m","--model")) {
      if ($i + 1 -ge $rest.Count) { throw "Missing model after $a" }
      $model = $rest[$i+1]
      $i += 2
      continue
    }
    if ($a -eq "--json") {
      $asJson = $true
      $i += 1
      continue
    }
    $promptParts.Add($a)
    $i += 1
  }

  $prompt = ($promptParts -join " ").Trim()
  if (-not $prompt) { throw "Empty prompt." }

  Invoke-OllamaGenerate -Prompt $prompt -Model $model -Json:$asJson
  exit 0
}

# ------------------------- Dispatch -------------------------

$repoRoot   = Get-RepoRoot
$commandsDir = Join-Path $PSScriptRoot "commands"
$cmdMap     = Get-CommandMap -CommandsDir $commandsDir

$argv = @($Args)
$sub  = if ($argv.Count -gt 0) { $argv[0].ToLowerInvariant() } else { "help" }
$rest = if ($argv.Count -gt 1) { @($argv[1..($argv.Count-1)]) } else { @() }

switch ($sub) {
  "help"     { Show-Usage -CommandMap $cmdMap -CommandsDir $commandsDir; exit 0 }
  "-h"       { Show-Usage -CommandMap $cmdMap -CommandsDir $commandsDir; exit 0 }
  "--help"   { Show-Usage -CommandMap $cmdMap -CommandsDir $commandsDir; exit 0 }

  "ask"      { Invoke-Ask -Rest $rest }

  default {
    if ($sub -eq "help" -and $rest.Count -ge 1) {
      Show-CommandHelp -Command $rest[0] -CommandMap $cmdMap
    }

    if ($cmdMap.ContainsKey($sub)) {
      & $cmdMap[$sub] @rest
      exit (Get-ExitCode)
    }

    Write-Host "Unknown command: $sub`n"
    Show-Usage -CommandMap $cmdMap -CommandsDir $commandsDir
    exit 2
  }
}
