# write_bootstrap_status.ps1
# PS 5.1+ compatible. Generates pre-Core status.json shaped like /v1/status where possible.

[CmdletBinding()]
param(
  [string]$AetherRoot = "D:\Aetherforge",
  [string]$OllamaBaseUrl = "http://127.0.0.1:11434",
  [string]$PinnedPath = "",
  [string]$SettingsPath = "",
  [string]$GpuEvidencePath = "",
  [string]$OutPath = ""
)

$ErrorActionPreference = "Stop"

$CfgDir = Join-Path $AetherRoot "config"
$LogDir = Join-Path $AetherRoot "logs\bootstrap"

if ([string]::IsNullOrWhiteSpace($PinnedPath))      { $PinnedPath      = Join-Path $CfgDir "pinned.yaml" }
if ([string]::IsNullOrWhiteSpace($SettingsPath))    { $SettingsPath    = Join-Path $CfgDir "settings.yaml" }
if ([string]::IsNullOrWhiteSpace($GpuEvidencePath)) { $GpuEvidencePath = Join-Path $LogDir "gpu_evidence.txt" }
if ([string]::IsNullOrWhiteSpace($OutPath))         { $OutPath         = Join-Path $LogDir "status.json" }

New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

function ReadFileOrNull([string]$p) {
  if (Test-Path $p) { return (Get-Content $p -Raw) }
  return $null
}

function TrimYamlScalar([string]$s) {
  if ($null -eq $s) { return $null }
  $t = $s.Trim()
  # strip inline comment if unquoted
  if ($t.StartsWith('"') -and $t.EndsWith('"') -and $t.Length -ge 2) { return $t.Substring(1, $t.Length-2) }
  if ($t.StartsWith("'") -and $t.EndsWith("'") -and $t.Length -ge 2) { return $t.Substring(1, $t.Length-2) }
  # basic inline comment stripping (best-effort)
  $hash = $t.IndexOf(" #")
  if ($hash -ge 0) { $t = $t.Substring(0, $hash).Trim() }
  return $t
}

function ExtractPinnedGeneralFast([string]$yamlText) {
  # Minimal YAML scanner to find models.general.fast.{tag,digest,required}
  $result = @{
    tag      = $null
    digest   = $null
    required = $null
  }

  if ([string]::IsNullOrWhiteSpace($yamlText)) { return $result }

  $stack = New-Object System.Collections.Generic.List[object]
  # stack items: @{ indent = <int>; key = <string> }

  $lines = $yamlText -split "`n"
  foreach ($raw in $lines) {
    $line = $raw.TrimEnd("`r")

    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $trim = $line.TrimStart()
    if ($trim.StartsWith("#")) { continue }

    # Key/value line?
    $m = [regex]::Match($line, '^(?<indent>\s*)(?<key>[A-Za-z0-9_]+)\s*:\s*(?<val>.*)$')
    if (-not $m.Success) { continue }

    $indent = $m.Groups["indent"].Value.Length
    $key = $m.Groups["key"].Value
    $valRaw = $m.Groups["val"].Value
    $val = TrimYamlScalar $valRaw

    # pop stack to parent indent
    for ($i = $stack.Count - 1; $i -ge 0; $i--) {
      if ($stack[$i].indent -lt $indent) { break }
      $stack.RemoveAt($i)
    }

    # push this mapping key if it starts a block (no scalar)
    $isBlock = [string]::IsNullOrWhiteSpace($val)
    if ($isBlock) {
      $stack.Add(@{ indent = $indent; key = $key })
      continue
    }

    # current path = stack keys + this key
    $pathKeys = @()
    foreach ($it in $stack) { $pathKeys += [string]$it.key }

    # We only care when inside models/general/fast
    $inModelsGeneralFast = ($pathKeys.Count -ge 3 -and
      $pathKeys[-3] -eq "models" -and
      $pathKeys[-2] -eq "general" -and
      $pathKeys[-1] -eq "fast")

    if (-not $inModelsGeneralFast) { continue }

    if ($key -eq "tag") {
      $result.tag = $val
    }
    elseif ($key -eq "digest") {
      if ($null -ne $val) {
        $d = $val.Trim()
        if ($d.StartsWith("sha256:")) { $d = $d.Substring(7) }
        $d = $d.ToLowerInvariant()
        $result.digest = $d
      }
    }
    elseif ($key -eq "required") {
      if ($null -ne $val) {
        $v = $val.Trim().ToLowerInvariant()
        if ($v -eq "true")  { $result.required = $true }
        if ($v -eq "false") { $result.required = $false }
      }
    }
  }

  return $result
}

function Coalesce([object]$v, [string]$fallback) {
  if ($null -eq $v) { return $fallback }
  $s = [string]$v
  if ([string]::IsNullOrWhiteSpace($s)) { return $fallback }
  return $s
}

$capturedUtc = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")

# --- Ollama reachability + version ---
$ollamaReachable = $false
$ollamaVersion = $null
try {
  $v = Invoke-RestMethod -Method Get -Uri ($OllamaBaseUrl.TrimEnd('/') + "/api/version") -TimeoutSec 3
  $ollamaReachable = $true
  if ($null -ne $v.version) { $ollamaVersion = [string]$v.version }
} catch {
  $ollamaReachable = $false
}

# --- Files existence ---
$pinnedExists = Test-Path $PinnedPath
$settingsExists = Test-Path $SettingsPath

# --- Pins match + digest match ---
$pinsMatch = $false
$modelDigestsMatch = $false
$pinsDetail = @()

if ($pinnedExists) {
  $pinnedText = Get-Content $PinnedPath -Raw
  $p = ExtractPinnedGeneralFast $pinnedText

  $hasTag = -not [string]::IsNullOrWhiteSpace($p.tag)
  $hasDig = -not [string]::IsNullOrWhiteSpace($p.digest) -and ($p.digest -match '^[0-9a-f]{64}$')
  $hasReq = ($p.required -eq $true)

  $pinsMatch = ($hasTag -and $hasDig -and $hasReq)

  if (-not $pinsMatch) {
    $pinsDetail += ("general.fast pinned entry incomplete: tag=" + (Coalesce $p.tag "<missing>") +
      " digest=" + (Coalesce $p.digest "<missing>") +
      " required=" + (Coalesce $p.required "<missing>"))
  } else {
    if ($ollamaReachable) {
      try {
        $tags = Invoke-RestMethod -Method Get -Uri ($OllamaBaseUrl.TrimEnd('/') + "/api/tags") -TimeoutSec 5
        $models = $tags.models
        if ($null -eq $models) { $models = $tags.Models } # defensive

        $hit = $null
        if ($models) { $hit = $models | Where-Object { $_.name -eq $p.tag } | Select-Object -First 1 }

        $live = $null
        if ($hit -and $hit.digest) {
          $live = ([string]$hit.digest).ToLowerInvariant()
          if ($live.StartsWith("sha256:")) { $live = $live.Substring(7) }
        }

        $modelDigestsMatch = ($null -ne $live -and ($live -eq $p.digest))
        $pinsDetail += ("general.fast tag=" + $p.tag + " pinned=" + $p.digest + " live=" + (Coalesce $live "<missing>"))
      } catch {
        $modelDigestsMatch = $false
        $pinsDetail += ("general.fast tag=" + $p.tag + " pinned=" + $p.digest + " live=<error fetching /api/tags>")
      }
    } else {
      $modelDigestsMatch = $false
      $pinsDetail += ("general.fast tag=" + $p.tag + " pinned=" + $p.digest + " live=<ollama not reachable>")
    }
  }
} else {
  $pinsMatch = $false
  $modelDigestsMatch = $false
  $pinsDetail += "<missing pinned.yaml>"
}

# --- GPU visible (WSL) + evidence (file contents) ---
$gpuVisible = $false
try {
  # no profiles/rc for determinism
  & wsl -- bash --noprofile --norc -c "nvidia-smi -L >/dev/null 2>&1" | Out-Null
  $gpuVisible = ($LASTEXITCODE -eq 0)
} catch {
  $gpuVisible = $false
}
$gpuEvidence = ReadFileOrNull $GpuEvidencePath

# --- Construct deterministic object ---
$status = [ordered]@{
  schema_version = 1
  captured_utc   = $capturedUtc

  ollama = [ordered]@{
    reachable = $ollamaReachable
    version   = $ollamaVersion
    base_url  = $OllamaBaseUrl
  }

  pins = [ordered]@{
    pinned_yaml_path    = $PinnedPath
    pins_match          = $pinsMatch
    model_digests_match = $modelDigestsMatch
    detail              = $pinsDetail
  }

  gpu = [ordered]@{
    visible  = $gpuVisible
    evidence = $gpuEvidence
  }

  files = [ordered]@{
    pinned_exists   = $pinnedExists
    settings_exists = $settingsExists
  }
}

($status | ConvertTo-Json -Depth 10) | Out-File -FilePath $OutPath -Encoding utf8
Write-Host ("Wrote: " + $OutPath)
