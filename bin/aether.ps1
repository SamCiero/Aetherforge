[CmdletBinding(PositionalBinding = $false)]
param(
  [Parameter(ValueFromRemainingArguments = $true)]
  [string[]] $Args
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Show-Usage {
@"
Usage:
  aether status
  aether start
  aether dev-core
  aether ask <prompt...> [-m <model>] [--json]

Notes:
  - Canonical base URLs: http://127.0.0.1:<port> (avoid localhost for PowerShell web cmdlets)
"@ | Write-Host
}

function Strip-Ansi([string] $s) {
  # Remove ANSI escape sequences like ESC[...m and stray cursor controls
  return ($s -replace "`e\[[0-9;]*[A-Za-z]", "")
}

function Invoke-OllamaGenerate {
  param(
    [Parameter(Mandatory=$true)] [string] $Prompt,
    [string] $Model = "qwen2.5:7b-instruct",
    [switch] $Json
  )

  $uri = "http://127.0.0.1:11434/api/generate"
  $body = @{ model = $Model; prompt = $Prompt; stream = $false } | ConvertTo-Json -Compress -Depth 10

  # Prefer Windows call (fast + no WSL quoting pain). Fallback to WSL curl if needed.
  try {
    $resp = Invoke-RestMethod -NoProxy -TimeoutSec 120 -Method Post -Uri $uri -ContentType "application/json" -Body $body
    if ($Json) { return ($resp | ConvertTo-Json -Compress -Depth 10) }
    return $resp.response
  }
  catch {
    # WSL fallback (robust prompt passing via base64)
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
    $bash = $bash.Replace("`r","")
    $raw = (wsl.exe -- bash -lc $bash) -join "`n"
    $raw = Strip-Ansi $raw

    if ($Json) { return $raw.Trim() }

    try { return ((ConvertFrom-Json $raw).response) }
    catch { return $raw.Trim() }
  }
}

$bin = $PSScriptRoot
$sub = if ($Args.Count -gt 0) { $Args[0].ToLowerInvariant() } else { "help" }
$rest = if ($Args.Count -gt 1) { $Args[1..($Args.Count-1)] } else { @() }

switch ($sub) {
  "status" {
    & (Join-Path $bin "aetherforge.ps1") -Cmd status
    exit $LASTEXITCODE
  }

  "start" {
    # For now: "start" == dev-core runner (long-running)
    & (Join-Path $bin "dev-core.ps1") @rest
    exit $LASTEXITCODE
  }

  "dev-core" {
    & (Join-Path $bin "dev-core.ps1") @rest
    exit $LASTEXITCODE
  }

  "ask" {
    if ($rest.Count -lt 1) { Show-Usage; exit 2 }

    $model = "qwen2.5:7b-instruct"
    $asJson = $false

    # tiny flag parser: -m <model> and --json
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

  "help" { Show-Usage; exit 0 }
  "-h" { Show-Usage; exit 0 }
  "--help" { Show-Usage; exit 0 }

  default {
    Write-Host "Unknown subcommand: $sub`n"
    Show-Usage
    exit 2
  }
}
