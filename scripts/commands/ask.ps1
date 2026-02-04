# D:\Aetherforge\scripts\commands\ask.ps1

<#
.SYNOPSIS
  Send a prompt to the local Ollama server and print the generated response.
#>

[CmdletBinding(PositionalBinding = $false)]
param(
  [switch] $Help,

  [Parameter(ValueFromRemainingArguments = $true)]
  [string[]] $Args
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Remove-Ansi([string] $s) {
  return ($s -replace "`e\[[0-?]*[ -/]*[@-~]", "")
}

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

    try {
      if ($Json) { return $raw.Trim() }
      return ((ConvertFrom-Json $raw).response)
    }
    catch {
      return $raw.Trim()
    }
    finally {
      Remove-Item Env:PROMPT_B64, Env:MODEL -ErrorAction SilentlyContinue
    }
  }
}

function Show-AskUsage {
@"
Usage:
  aether ask <prompt...> [-m <model>] [--json]
Notes:
  - Use '--' to stop option parsing, e.g.: aether ask -- --json is literal
"@ | Write-Host
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

    if ($a -eq "--") {
      if ($i + 1 -lt $rest.Count) {
        $promptParts.AddRange(@($rest[($i+1)..($rest.Count-1)]))
      }
      break
    }

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

  $out = Invoke-OllamaGenerate -Prompt $prompt -Model $model -Json:$asJson
  Write-Output $out
}

$argv = @($Args)
if ($Help -or $argv.Count -eq 0 -or $argv[0] -in @("-h","--help")) {
  Show-AskUsage
  exit 0
}

Invoke-Ask -Rest $argv
exit 0
