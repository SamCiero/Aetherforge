# M0 — Bootstrap Substrate Checklist

## Objective
Prove the **runtime substrate** is real and repeatable:
- WSL2 + mirrored networking
- NVIDIA GPU visible in WSL
- Ollama reachable on canonical loopback
- **At least one pinned model digest** (the fallback target, default `general.fast`)
- Survives Windows reboot + WSL restart with a repeatable smoke test
- Capture an initial **bootstrap status snapshot** (pre-Core) that is shaped to match the eventual `/v1/status` contract as closely as possible

> Canonical local endpoints (per spec)
> - Ollama (WSL): `http://127.0.0.1:11434`
> - Core (later): `http://127.0.0.1:8484`
> - Avoid `localhost` for Windows clients.

---

## Tasks

### 1) WSL2 substrate + networking
- [x] Enable WSL2 on Windows
- [x] Install Ubuntu LTS distro (WSL)
- [x] Configure WSL2 mirrored networking
- [ ] Capture evidence that mirrored networking is enabled (command output / config snippet) and save under:
  - `D:\Aetherforge\logs\bootstrap\wsl_networking.txt` (or similar)

### 2) GPU availability (WSL)
- [x] Verify NVIDIA GPU inside WSL (`nvidia-smi`)
- [x] Capture deterministic GPU evidence (e.g., `nvidia-smi --query-gpu=name,driver_version --format=csv,noheader`) and save under:
  - `D:\Aetherforge\logs\bootstrap\gpu_evidence.txt`
- [x] Evidence of GPU-backed inference captured (utilization during inference OR other deterministic evidence)

### 3) Ollama runtime (WSL)
- [x] Install Ollama (Linux) inside WSL
- [x] Confirm Ollama is reachable at canonical loopback:
  - `curl http://127.0.0.1:11434/api/version`
- [x] Configure/verify Ollama model dir (canonical): `/var/lib/ollama`
- [x] Record Ollama version for pinning metadata

### 4) Windows root skeleton (human-editable + artifacts)
- [x] Create Windows root skeleton:
  - `D:\Aetherforge\config\`
  - `D:\Aetherforge\logs\bootstrap\`
  - `D:\Aetherforge\exports\`
  - `D:\Aetherforge\bin\`

### 5) Initial config scaffolding (spec schema v1)
- [x] Create `D:\Aetherforge\config\settings.yaml` (schema v1) with **all required blocks present**. Even if values are placeholders, include the following keys:
  - `schema_version: 1`
  - `ports.core_bind_url`: `http://127.0.0.1:8484`
  - `ports.ollama_base_url`: `http://127.0.0.1:11434`
  - `defaults.role`: `general` (must be `general` or `coding`; do not set `agent`)
  - `defaults.tier`: `fast` (must be `fast` or `thinking`)
  - `pins.mode`: `fallback` (recommended for early dev) OR `strict` (allowed)
  - `pins.fallback_role`: `general` (must be `general` or `coding`)
  - `pins.fallback_tier`: `fast` (must be `fast` or `thinking`)
  - `profiles`: include `general` and `coding`; include `agent` **only when** `agent.enabled=true`
  - `generation`: include `by_profile` with nested `general` and `coding` tiers; placeholder values may be null
  - `autostart`: set `enabled: false` and `windows_scheduled_task_name: null`
  - `boundary`: skeleton with `roots`, `bridge_rules`, `allow_write_under_wsl`, `allow_read_under_wsl`, `block_reparse_points` (placeholders OK; structure must be valid)
  - `agent`: set `enabled: false`, `require_plan_approval: true`, `allow_tools: []`
- [x] Create `D:\Aetherforge\config\pinned.yaml` (schema v1 per updated spec)
  - [x] Record `captured_utc` (ISO-8601 UTC)
  - [x] Record `ollama.version`
  - [x] Ensure digest normalization rules are followed:
    - lowercase 64-hex
    - no `sha256:` prefix stored

### 6) Baseline pinned model (fallback target)
Goal: pin at least the fallback target (default `general.fast`).

- [x] Pull baseline model tag: `qwen2.5:7b-instruct`
- [x] Resolve baseline model digest from Ollama `/api/tags`
- [x] Record in `pinned.yaml` under:
  - `models.general.fast.tag = "qwen2.5:7b-instruct"`
  - `models.general.fast.digest = "<64-hex>"`
  - `models.general.fast.required = true`
  - Note: the fallback target should always be marked `required: true` so fallback mode remains deterministic.
- [x] Verify pinned digest matches live digest (`/api/tags`) via scripted check (expected: `OK`)
- [x] Run a smoke prompt via Ollama API (reachability + model works)
- [x] (Optional but spec-friendly) Add placeholder entries with `digest: null` for:
  - `general.thinking`, `coding.fast`, `coding.thinking`
  - Mark `required: false` for placeholders at M0 (only the fallback entry should be required this early).

### 7) Bootstrap status snapshot (pre-Core)
> M0 happens before Core exists, so this snapshot is *pre-Core* and stored as a file.
> Shape it to mirror the eventual `/v1/status` schema wherever possible so later comparisons are easy.

- [x] Capture a bootstrap status snapshot JSON and save under:
  - `D:\Aetherforge\logs\bootstrap\status.json`
- [ ] Ensure the snapshot includes (where applicable):
  - `schema_version: 1`
  - `captured_utc`
  - `ollama.reachable`, `ollama.version`, `ollama.base_url`
  - `pins.pinned_yaml_path`, `pins.pins_match` (true/false), `pins.model_digests_match` (true/false), `pins.detail`
  - `gpu.visible`, `gpu.evidence`
  - `files.pinned_exists`, `files.settings_exists`
  - (Core/db/tailnet can be absent or null/false in M0, but keep it deterministic if included)

---

## Acceptance
- [x] GPU is visible inside WSL (`nvidia-smi` succeeds) + evidence saved
- [x] Ollama responds successfully via canonical loopback (`127.0.0.1:11434`)
- [x] Baseline model responds successfully via Ollama API
- [x] `pinned.yaml` exists and contains:
  - [x] `schema_version: 1`
  - [x] `captured_utc`
  - [x] `ollama.version`
  - [x] `models.general.fast.tag` + `models.general.fast.digest` (non-null)
- [x] Pinned digest matches live digest from `/api/tags`
- [x] Works after Windows reboot + WSL restart (repeat smoke prompt + snapshot update)

---

## Notes / quirks discovered (must remain true going forward)
- Prefer canonical loopback host `127.0.0.1` (avoid `localhost` for Windows tooling due to proxy/WPAD latency).
- When passing multi-line scripts from PowerShell → `wsl.exe -- bash -lc`, strip CRs:
  - `$cmd = $cmd.Replace("`r","")`
- WAL sidecars may not appear until write activity (relevant in later milestones).

---

## Artifacts
- `D:\Aetherforge\config\pinned.yaml`
- `D:\Aetherforge\config\settings.yaml` (new requirement per spec; minimal valid skeleton is sufficient for M0)
- `D:\Aetherforge\logs\bootstrap\status.json`
- `D:\Aetherforge\logs\bootstrap\gpu_evidence.txt` (or equivalent)
- Baseline model tag+digest recorded and verified
