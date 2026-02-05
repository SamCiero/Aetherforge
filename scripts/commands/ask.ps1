# D:\Aetherforge\scripts\commands\ask.ps1

<#
.SYNOPSIS
  Send a prompt to Core (/v1/chat SSE) and print the generated response.

  This is a dev/smoke-test command: it exercises the product path
  (pins -> persistence -> SSE streaming) rather than calling Ollama directly.
#>

[CmdletBinding(PositionalBinding = $false)]
param(
  [switch] $Help,

  [Parameter(ValueFromRemainingArguments = $true)]
  [string[]] $Args
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-CoreCreateConversation {
  param(
    [Parameter(Mandatory = $true)] [string] $BaseUrl,
    [Parameter(Mandatory = $true)] [string] $Role,
    [Parameter(Mandatory = $true)] [string] $Tier,
    [string] $Title
  )

  $uri = ($BaseUrl.TrimEnd("/") + "/v1/conversations")
  $body = @{ role = $Role; tier = $Tier; title = $Title } | ConvertTo-Json -Compress -Depth 10
  return Invoke-RestMethod -NoProxy -TimeoutSec 10 -Method Post -Uri $uri -ContentType "application/json" -Body $body
}

function Invoke-CoreChatSse {
  param(
    [Parameter(Mandatory = $true)] [string] $BaseUrl,
    [Parameter(Mandatory = $true)] [int] $ConversationId,
    [Parameter(Mandatory = $true)] [string] $Content
  )

  $uri = ($BaseUrl.TrimEnd("/") + "/v1/chat")
  $payload = @{ conversation_id = $ConversationId; content = $Content } | ConvertTo-Json -Compress -Depth 10

  $handler = New-Object System.Net.Http.HttpClientHandler
  $handler.UseProxy = $false
  $handler.Proxy = $null

  $client = [System.Net.Http.HttpClient]::new($handler)
  $client.Timeout = [System.Threading.Timeout]::InfiniteTimeSpan

  $msg = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Post, $uri)
  $msg.Headers.Accept.Clear()
  $msg.Headers.Accept.Add([System.Net.Http.Headers.MediaTypeWithQualityHeaderValue]::new("text/event-stream"))
  $msg.Content = [System.Net.Http.StringContent]::new($payload, [Text.Encoding]::UTF8, "application/json")

  $resp = $null
  $stream = $null
  $reader = $null

  try {
    $resp = $client.SendAsync($msg, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
    if (-not $resp.IsSuccessStatusCode) {
      $txt = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
      throw "Core /v1/chat failed: HTTP $([int]$resp.StatusCode) $($resp.ReasonPhrase). $txt"
    }

    $stream = $resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
    $reader = New-Object System.IO.StreamReader($stream, [Text.Encoding]::UTF8)

    $meta = $null
    $assistant = New-Object System.Text.StringBuilder

    $curEvent = $null
    $data = New-Object System.Text.StringBuilder
    $done = $false

    while (-not $done) {
      $line = $reader.ReadLine()
      if ($null -eq $line) { break }

      if ($line -eq "") {
        if ($curEvent -and $data.Length -gt 0) {
          $json = $data.ToString().TrimEnd("`n")
          switch ($curEvent) {
            "meta" {
              $meta = ConvertFrom-Json $json
            }
            "delta" {
              $d = ConvertFrom-Json $json
              if ($d.delta_text) { [void]$assistant.Append([string]$d.delta_text) }
            }
            "done" {
              $done = $true
            }
            "error" {
              $e = ConvertFrom-Json $json
              $code = $e.code
              $msg2 = $e.message
              $detail = $e.detail
              $hint = $e.hint

              $extra = ""
              if ($detail) { $extra += "`nDetail: $detail" }
              if ($hint) { $extra += "`nHint: $hint" }
              throw ("{0}: {1}{2}" -f $code, $msg2, $extra)
            }
          }
        }

        $curEvent = $null
        [void]$data.Clear()
        continue
      }

      if ($line.StartsWith("event:")) {
        $curEvent = $line.Substring(6).Trim()
        continue
      }

      if ($line.StartsWith("data:")) {
        [void]$data.AppendLine($line.Substring(5).TrimStart())
        continue
      }
    }

    return [pscustomobject]@{
      meta = $meta
      text = $assistant.ToString()
    }
  }
  finally {
    try { if ($reader -ne $null) { $reader.Dispose() } } catch {}
    try { if ($stream -ne $null) { $stream.Dispose() } } catch {}
    try { if ($resp -ne $null) { $resp.Dispose() } } catch {}
    try { $client.Dispose() } catch {}
  }
}

function Show-AskUsage {
@"
Usage:
  aether ask <prompt...> [--role <role>] [--tier <tier>] [--profile <role>/<tier>] [-m <role>/<tier>] [--id <conversationId>] [--base-url <url>] [--json]
Notes:
  - Use '--' to stop option parsing, e.g.: aether ask -- --json is literal
Defaults:
  --role general
  --tier fast
  --base-url http://127.0.0.1:8484
"@ | Write-Host
}

function Parse-Profile {
  param([Parameter(Mandatory = $true)][string] $Value)

  $vv = $Value
  if ($null -eq $vv) { $vv = "" }
  $v = $vv.Trim().ToLowerInvariant()
  if (-not $v) { throw "Empty profile." }

  # Accept: role/tier, role:tier, role-tier, role_tier
  $m = [regex]::Match($v, '^(?<r>[a-z]+)[\/:_\-](?<t>[a-z]+)$')
  if (-not $m.Success) { throw "Invalid profile '$Value'. Expected <role>/<tier> (e.g. general/fast)." }

  $r = $m.Groups['r'].Value
  $t = $m.Groups['t'].Value

  if ($r -notin @("general","coding","agent")) { throw "Invalid role '$r'. Expected general|coding|agent." }
  if ($t -notin @("fast","thinking")) { throw "Invalid tier '$t'. Expected fast|thinking." }

  return @($r, $t)
}

function Invoke-Ask {
  param([string[]] $Rest)

  $rest = @($Rest)
  if ($rest.Count -lt 1) { throw "Usage: aether ask <prompt...> [--role <role>] [--tier <tier>] [--profile <role>/<tier>] [--json]" }

  $baseUrl = "http://127.0.0.1:8484"
  $role    = "general"
  $tier    = "fast"
  $title   = $null
  $id      = 0
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

    if ($a -in @("-m","--model","--profile")) {
      if ($i + 1 -ge $rest.Count) { throw "Missing profile after $a" }
      $p = Parse-Profile -Value $rest[$i+1]
      $role = $p[0]
      $tier = $p[1]
      $i += 2
      continue
    }

    if ($a -eq "--role") {
      if ($i + 1 -ge $rest.Count) { throw "Missing role after $a" }
      $rv = $rest[$i+1]
      if ($null -eq $rv) { $rv = "" }
      $role = $rv.Trim().ToLowerInvariant()
      $i += 2
      continue
    }

    if ($a -eq "--tier") {
      if ($i + 1 -ge $rest.Count) { throw "Missing tier after $a" }
      $tv = $rest[$i+1]
      if ($null -eq $tv) { $tv = "" }
      $tier = $tv.Trim().ToLowerInvariant()
      $i += 2
      continue
    }

    if ($a -in @("--id","--conversation-id")) {
      if ($i + 1 -ge $rest.Count) { throw "Missing conversation id after $a" }
      $id = [int]$rest[$i+1]
      $i += 2
      continue
    }

    if ($a -in @("--title")) {
      if ($i + 1 -ge $rest.Count) { throw "Missing title after $a" }
      $title = $rest[$i+1]
      $i += 2
      continue
    }

    if ($a -in @("--base-url","--baseUrl")) {
      if ($i + 1 -ge $rest.Count) { throw "Missing base url after $a" }
      $baseUrl = $rest[$i+1]
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

  if ($role -notin @("general","coding","agent")) { throw "Invalid role '$role'. Expected general|coding|agent." }
  if ($tier -notin @("fast","thinking")) { throw "Invalid tier '$tier'. Expected fast|thinking." }

  $bv = $baseUrl
  if ($null -eq $bv) { $bv = "" }
  $base = $bv.Trim()
  if (-not $base) { throw "Empty --base-url." }

  $conversationId = $id
  $created = $null
  if ($conversationId -le 0) {
    $created = Invoke-CoreCreateConversation -BaseUrl $base -Role $role -Tier $tier -Title $title
    $conversationId = [int]$created.id
  }

  $res = Invoke-CoreChatSse -BaseUrl $base -ConversationId $conversationId -Content $prompt
  $meta = $res.meta
  $text = $res.text

  if ($asJson) {
    $cid = $conversationId
    $mid = $null
    $mtag = $null
    $mdig = $null

    if ($meta -ne $null) {
      if ($meta.conversation_id) { $cid = [int]$meta.conversation_id }
      if ($meta.message_id) { $mid = [int]$meta.message_id }
      $mtag = $meta.model_tag
      $mdig = $meta.model_digest
    }

    $out = [ordered]@{
      conversation_id = $cid
      message_id      = $mid
      model_tag       = $mtag
      model_digest    = $mdig
      response        = $text
    }
    if ($created) {
      $out.created_conversation = $true
      $out.role = $role
      $out.tier = $tier
    }
    $out | ConvertTo-Json -Compress -Depth 10 | Write-Output
  } else {
    Write-Output $text
  }
}

$argv = @($Args)
if ($Help -or $argv.Count -eq 0 -or $argv[0] -in @("-h","--help")) {
  Show-AskUsage
  exit 0
}

try {
  Invoke-Ask -Rest $argv
  exit 0
}
catch {
  Write-Host "Ask failed." -ForegroundColor Red
  Write-Host ("{0}: {1}" -f $_.Exception.GetType().Name, $_.Exception.Message)
  exit 1
}
