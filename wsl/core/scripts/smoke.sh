#!/usr/bin/env bash
set -euo pipefail

# Aetherforge WSL smoke test
# Validates M1 Core contract end-to-end (including SSE) from inside WSL.
#
# Default targets:
#   Core:   http://127.0.0.1:8484
#   Ollama: http://127.0.0.1:11434

usage() {
  cat <<'TXT'
Usage: smoke.sh [options]

Options:
  --base-url <url>       Core base URL (default: http://127.0.0.1:8484)
  --ollama-url <url>     Ollama base URL (default: http://127.0.0.1:11434)
  --role <role>          Conversation role (default: general)
  --tier <tier>          Conversation tier (default: fast)
  --timeout <sec>        SSE chat timeout seconds (default: 60)
  --skip-gpu             Do not fail if nvidia-smi is missing
  --skip-export-check    Do not verify files exist on disk after export
  -h, --help             Show help

Exit codes:
  0  all checks passed
  1  a required check failed

Notes:
  - This script is intentionally strict: missing endpoints = failure.
  - Windows-side reachability should be validated separately with curl.exe.
TXT
}

log() { printf '[smoke] %s\n' "$*"; }
die() { printf '[smoke] ERROR: %s\n' "$*" >&2; exit 1; }

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd -- "$SCRIPT_DIR/../../.." && pwd)"

BASE_URL="http://127.0.0.1:8484"
OLLAMA_URL="http://127.0.0.1:11434"
ROLE="general"
TIER="fast"
SSE_TIMEOUT=60
SKIP_GPU=0
SKIP_EXPORT_CHECK=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --base-url) BASE_URL="$2"; shift 2;;
    --ollama-url) OLLAMA_URL="$2"; shift 2;;
    --role) ROLE="$2"; shift 2;;
    --tier) TIER="$2"; shift 2;;
    --timeout) SSE_TIMEOUT="$2"; shift 2;;
    --skip-gpu) SKIP_GPU=1; shift;;
    --skip-export-check) SKIP_EXPORT_CHECK=1; shift;;
    -h|--help) usage; exit 0;;
    *) die "Unknown arg: $1";;
  esac
done

[[ -f "$ROOT/Aetherforge.sln" ]] || die "Repo root not found (expected Aetherforge.sln). Got: $ROOT"

need_cmd() {
  command -v "$1" >/dev/null 2>&1 || die "Missing required command: $1"
}

need_cmd curl
need_cmd python3

http_json() {
  # Usage: http_json <method> <url> <json_body_or_empty>
  local method="$1" url="$2" body="$3"
  if [[ -n "$body" ]]; then
    curl -fsS -X "$method" "$url" -H 'Content-Type: application/json' --data "$body"
  else
    curl -fsS -X "$method" "$url"
  fi
}

json_get() {
  # Usage: json_get <json> <python_expr_that_prints_value>
  local json="$1" expr="$2"
  python3 - <<PY
import json,sys
obj=json.loads(sys.stdin.read())
$expr
PY
}

assert_contains() {
  local hay="$1" needle="$2" label="$3"
  grep -q -- "$needle" <<<"$hay" || die "$label: missing '$needle'"
}

log "Repo root: $ROOT"
log "Core:  $BASE_URL"
log "Ollama:$OLLAMA_URL"

# 1) GPU visibility (optional)
if command -v nvidia-smi >/dev/null 2>&1; then
  nvidia-smi >/dev/null 2>&1 || die "nvidia-smi present but failed"
  log "GPU: nvidia-smi OK"
else
  if [[ $SKIP_GPU -eq 1 ]]; then
    log "GPU: nvidia-smi missing (skipped)"
  else
    die "nvidia-smi missing (use --skip-gpu to bypass)"
  fi
fi

# 2) Ollama reachable
log "Checking Ollama API..."
http_json GET "$OLLAMA_URL/api/tags" "" >/dev/null || die "Ollama not reachable at $OLLAMA_URL"
log "Ollama: OK"

# 3) Core status reachable
log "Checking Core /v1/status..."
status_json="$(http_json GET "$BASE_URL/v1/status" "")" || die "Core not reachable at $BASE_URL"
python3 -c 'import json,sys; json.loads(sys.stdin.read());' <<<"$status_json" || die "/v1/status did not return valid JSON"
log "Core: OK"

# 4) Create conversation
log "Creating conversation (role=$ROLE tier=$TIER)..."
now_utc="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
create_body="{\"role\":\"$ROLE\",\"tier\":\"$TIER\",\"title\":\"smoke $now_utc\"}"
create_json="$(http_json POST "$BASE_URL/v1/conversations" "$create_body")" || die "POST /v1/conversations failed"
conv_id="$(json_get "$create_json" 'print(obj.get("id"))')"
[[ "$conv_id" =~ ^[0-9]+$ ]] || die "Create conversation returned invalid id: $conv_id"
log "Conversation id: $conv_id"

# 5) List + search
log "Listing conversations..."
list_json="$(http_json G
