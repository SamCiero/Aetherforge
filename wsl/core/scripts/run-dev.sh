#!/usr/bin/env bash
set -euo pipefail

# Aetherforge WSL dev runner
# - Starts Ollama (if needed)
# - Starts Core in dev mode (foreground by default)
# - Optional: run smoke tests against the running Core
#
# Canonical URLs:
#   Core:   http://127.0.0.1:8484
#   Ollama: http://127.0.0.1:11434

usage() {
  cat <<'TXT'
Usage: run-dev.sh [options]

Options:
  --root <path>          Repo root inside WSL (default: auto from script location)
  --core-url <url>       Core base URL (default: http://127.0.0.1:8484)
  --ollama-url <url>     Ollama base URL (default: http://127.0.0.1:11434)
  --no-ollama            Do not start/validate Ollama
  --watch                Use 'dotnet watch run' instead of 'dotnet run'
  --bg                   Run Core in background (writes logs to --core-log)
  --core-log <path>      Core log path when --bg is used (default: /tmp/aetherforge-core.dev.log)
  --smoke                Run smoke.sh after Core is reachable (requires --bg)
  --exit-after-smoke     When --smoke is used, stop Core after smoke completes
  -h, --help             Show help

Examples:
  ./wsl/core/scripts/run-dev.sh
  ./wsl/core/scripts/run-dev.sh --watch
  ./wsl/core/scripts/run-dev.sh --bg --smoke --exit-after-smoke
TXT
}

log() { printf '[run-dev] %s\n' "$*"; }
die() { printf '[run-dev] ERROR: %s\n' "$*" >&2; exit 1; }

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
ROOT=""
CORE_URL="http://127.0.0.1:8484"
OLLAMA_URL="http://127.0.0.1:11434"
NO_OLLAMA=0
USE_WATCH=0
BG=0
CORE_LOG="/tmp/aetherforge-core.dev.log"
SMOKE=0
EXIT_AFTER_SMOKE=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --root) ROOT="$2"; shift 2;;
    --core-url) CORE_URL="$2"; shift 2;;
    --ollama-url) OLLAMA_URL="$2"; shift 2;;
    --no-ollama) NO_OLLAMA=1; shift;;
    --watch) USE_WATCH=1; shift;;
    --bg) BG=1; shift;;
    --core-log) CORE_LOG="$2"; shift 2;;
    --smoke) SMOKE=1; shift;;
    --exit-after-smoke) EXIT_AFTER_SMOKE=1; shift;;
    -h|--help) usage; exit 0;;
    *) die "Unknown arg: $1";;
  esac
done

if [[ -z "$ROOT" ]]; then
  ROOT="$(cd -- "$SCRIPT_DIR/../../.." && pwd)"
fi

[[ -f "$ROOT/Aetherforge.sln" ]] || die "Repo root not found (expected Aetherforge.sln). Got: $ROOT"

CORE_PROJ="$ROOT/src/Aetherforge.Core/Aetherforge.Core.csproj"
[[ -f "$CORE_PROJ" ]] || die "Core project not found: $CORE_PROJ"

SMOKE_SH="$SCRIPT_DIR/smoke.sh"
[[ -f "$SMOKE_SH" ]] || die "smoke.sh not found: $SMOKE_SH"

if ! command -v dotnet >/dev/null 2>&1; then
  die "dotnet SDK not found in WSL PATH"
fi

ensure_curl() {
  command -v curl >/dev/null 2>&1 || die "curl not found; install it in WSL (apt-get install curl)"
}

http_ok() {
  local url="$1"
  curl -fsS --max-time 2 "$url" >/dev/null 2>&1
}

ensure_ollama() {
  if [[ $NO_OLLAMA -eq 1 ]]; then
    log "Skipping Ollama checks (--no-ollama)"
    return 0
  fi

  ensure_curl

  if http_ok "$OLLAMA_URL/api/tags"; then
    log "Ollama reachable at $OLLAMA_URL"
    return 0
  fi

  log "Ollama not reachable; attempting start..."

  if command -v systemctl >/dev/null 2>&1 && systemctl list-unit-files 2>/dev/null | grep -qE '^ollama\.service'; then
    if [[ ${EUID:-$(id -u)} -ne 0 ]]; then
      sudo systemctl start ollama || true
    else
      systemctl start ollama || true
    fi
  elif command -v ollama >/dev/null 2>&1; then
    # Fallback: run in background
    local pidfile="/tmp/aetherforge-ollama.pid"
    if [[ -f "$pidfile" ]] && kill -0 "$(cat "$pidfile")" 2>/dev/null; then
      log "ollama serve appears already running (pid $(cat "$pidfile"))"
    else
      nohup ollama serve > /tmp/aetherforge-ollama.dev.log 2>&1 &
      echo $! > "$pidfile"
      log "Started 'ollama serve' (pid $!)"
    fi
  else
    die "ollama not found and no systemd unit 'ollama.service' found"
  fi

  # Wait for Ollama
  for _ in {1..40}; do
    if http_ok "$OLLAMA_URL/api/tags"; then
      log "Ollama reachable at $OLLAMA_URL"
      return 0
    fi
    sleep 0.25
  done

  die "Ollama failed to become reachable at $OLLAMA_URL"
}

wait_for_core() {
  ensure_curl
  local url="$CORE_URL/v1/status"
  for _ in {1..60}; do
    if http_ok "$url"; then
      return 0
    fi
    sleep 0.25
  done
  return 1
}

start_core_bg() {
  local cmd=(dotnet run --project "$CORE_PROJ")
  if [[ $USE_WATCH -eq 1 ]]; then
    cmd=(dotnet watch run --project "$CORE_PROJ")
  fi

  log "Starting Core (background) => $CORE_URL"
  log "Logs: $CORE_LOG"

  # Force canonical URL at runtime (even if Program.cs has a default).
  export ASPNETCORE_URLS="$CORE_URL"
  export ASPNETCORE_ENVIRONMENT="Development"

  nohup "${cmd[@]}" >"$CORE_LOG" 2>&1 &
  echo $! > /tmp/aetherforge-core.pid
  log "Core pid: $(cat /tmp/aetherforge-core.pid)"
}

stop_core_bg() {
  local pidfile=/tmp/aetherforge-core.pid
  if [[ -f "$pidfile" ]]; then
    local pid
    pid="$(cat "$pidfile")"
    if kill -0 "$pid" 2>/dev/null; then
      log "Stopping Core (pid $pid)"
      kill "$pid" || true
      # wait a moment for shutdown
      for _ in {1..30}; do
        kill -0 "$pid" 2>/dev/null || break
        sleep 0.1
      done
      kill -0 "$pid" 2>/dev/null && kill -9 "$pid" 2>/dev/null || true
    fi
    rm -f -- "$pidfile"
  fi
}

run_smoke() {
  log "Running smoke tests"
  "$SMOKE_SH" --base-url "$CORE_URL" --ollama-url "$OLLAMA_URL"
}

ensure_ollama

if [[ $SMOKE -eq 1 && $BG -ne 1 ]]; then
  die "--smoke requires --bg (so Core can run while smoke executes)"
fi

if [[ $BG -eq 1 ]]; then
  start_core_bg

  if ! wait_for_core; then
    log "Core did not become reachable. Tail of $CORE_LOG:" >&2
    tail -n 200 "$CORE_LOG" >&2 || true
    exit 1
  fi

  log "Core reachable: $CORE_URL/v1/status"

  if [[ $SMOKE -eq 1 ]]; then
    run_smoke
    if [[ $EXIT_AFTER_SMOKE -eq 1 ]]; then
      stop_core_bg
      exit 0
    fi
  fi

  log "Core running in background. Tail logs with: tail -f $CORE_LOG"
  exit 0
fi

# Foreground mode
log "Starting Core (foreground) => $CORE_URL"
export ASPNETCORE_URLS="$CORE_URL"
export ASPNETCORE_ENVIRONMENT="Development"

if [[ $USE_WATCH -eq 1 ]]; then
  exec dotnet watch run --project "$CORE_PROJ"
else
  exec dotnet run --project "$CORE_PROJ"
fi
