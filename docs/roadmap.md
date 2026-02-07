# Aetherforge — Project Roadmap

This roadmap is authoritative for sequencing work on Aetherforge.
Each milestone has a dedicated checklist file with explicit completion gates.

Progression rule: do not start a milestone until the previous one is complete.

## Canonical local URL + PowerShell quirk
- Canonical loopback host is `127.0.0.1` for all local calls (Windows→WSL and Windows-local).
- Do **not** use `localhost` (PowerShell/WinHTTP proxy settings can route it and add large latency).
  - Prefer `Invoke-RestMethod http://127.0.0.1:<port>/... -NoProxy` for local calls.
  - For raw timing probes, prefer `curl.exe http://127.0.0.1:<port>/...`.

## Windows launcher contract (stable entrypoint + internal dispatcher)
Aetherforge has a stable user-facing entrypoint and an internal dispatcher surface:

- **Stable entrypoint (docs/checklists invoke this):**
  - `D:\Aetherforge\bin\aetherforge.ps1`
  - Thin wrapper; minimal logic; delegates into `scripts\aether.ps1`.

- **Internal dispatcher (implementation surface):**
  - `D:\Aetherforge\scripts\aether.ps1`
  - Dispatches subcommands implemented at `D:\Aetherforge\scripts\commands\*.ps1`.

Command semantics (steady intent):
- `aether dev-core` = start Core in dev-mode (CLI-only; persists across the project).
- `aether start` = reserved for launching the Desktop UI (M4+).
  - Until M4, `start` may remain a shim that indirectly runs `dev-core`.

> Note: interactive aliases like `aether <cmd>` are convenience. The contract is the stable entrypoint path above.

## Role/tier glossary (per Spec)
- Roles: `general`, `coding`, `agent`
  - `agent` is **gated** by `settings.yaml: agent.enabled` and only implemented at M6.
- General + Coding tiers: `fast`, `thinking`
- Agent tiers: `primary`, `verifier`
  - UX intent: “Agent” is a single mode selection; internally Core uses **primary** to do the work and **verifier** to verify it.
- Config default selector:
  - `settings.yaml: defaults.role ∈ {general, coding}`
  - `settings.yaml: defaults.tier ∈ {fast, thinking}`
  - `defaults.role=agent` is not allowed.

## Milestones

### M0 — Bootstrap Substrate
Foundation: WSL2, GPU passthrough, Ollama runtime, initial config scaffolding.
Goal: prove the platform assumptions.

Key deliverables (aligned with spec):
- WSL2 mirrored networking working.
- GPU visible in WSL (`nvidia-smi` evidence captured).
- Ollama installed and reachable at `http://127.0.0.1:11434`.
- Initial config scaffold created under `D:\Aetherforge\config\`:
  - `settings.yaml` (schema v1) with **all required blocks present** (even if minimally populated):
    - `ports`, `defaults` (general/coding only), `pins`, `profiles`, `generation`, `autostart`, `boundary`, `agent` (default disabled)
  - role profile files present per contract:
    - `config\profiles\general.yaml`, `config\profiles\coding.yaml`, `config\profiles\agent.yaml` (present but used only when `agent.enabled=true`)
- Initial `pinned.yaml` created (schema v1).
- At least one pinned, non-null digest for the **fallback target** specified in `settings.yaml` (`pins.fallback_role/tier`).

### M1 — Core Backend + Persistence
Linux-side Core service with stable REST + SSE contract, settings validation, pin manifest loading/verification, SQLite policy, boundary enforcement, deterministic exports.
Goal: stable backend contract before any UI.

Key deliverables:
- Settings loader/validator for `settings.yaml` (schema v1), including:
  - `ports.core_bind_url` and `ports.ollama_base_url` are absolute URLs using host `127.0.0.1`.
  - `defaults.role ∈ {general, coding}` and `defaults.tier ∈ {fast, thinking}`.
  - `pins.mode` (`strict|fallback`) + `pins.fallback_role/tier` (restricted to `general|coding` + `fast|thinking`).
  - `profiles` + `generation` blocks present and structurally valid (general/coding required; agent allowed only when `agent.enabled=true`).
  - `autostart` block present and structurally valid.
  - `agent` block present; `agent.enabled=false` by default.
  - boundary roots + allowlists + bridge rules (schema + validation).
- Pins loader/validator for `pinned.yaml` (schema v1) supporting:
  - null digests (unpinned placeholders)
  - optional `required` flag per entry
  - status-time evaluation: `pins_match` and `model_digests_match`
  - `agent.*` pins ignored for required-sets when `agent.enabled=false`
- Core API endpoints:
  - `GET /v1/status`
  - conversations: create, list/search, read, patch title
  - `POST /v1/chat` SSE streaming
  - `POST /v1/export/{id}` deterministic JSON + Markdown export
- SSE schema is stable and implemented (`meta/delta/done/error`).
- SQLite operational policy:
  - WAL enabled + verified
  - busy_timeout/retry rules
  - schema versioning/migrations
  - backup-copy rules (WAL/SHM inclusion or checkpoint-on-stop)
- Boundary enforcement:
  - deny writes outside allowlisted roots
  - block reparse points/symlinks/junctions when enabled
- Windows↔WSL reachability gate:
  - prove Windows host can call Core API reliably
  - document a bridge/proxy fallback if mirrored networking is insufficient

### M2 — General + Coding Roles (Core Assistant)
Implement General + Coding with Fast/Thinking tiers, pinned role/tier mapping, fallback behavior, offline-capable operation, acceptance tested via CLI/API harness.
Goal: functional assistant baseline for daily evaluation via terminal.

Key deliverables:
- Conversation creation pins `{role, tier, model_tag, model_digest}` deterministically from `pinned.yaml`.
- Fallback behavior:
  - `strict`: missing/unpinned role/tier fails conversation creation
  - `fallback`: missing/unpinned role/tier resolves to `pins.fallback_role/tier`
  - always truthful about the actual resolved model used
- SSE `meta` event includes `resolution` when fallback applies:
  - `fallback:<role>.<tier>`
- Offline validation gates:
  - models pulled
  - digests match
  - chats succeed without network
- Export includes model tag + digest and message `meta_json` where applicable.

### M3 — Windows Launcher + Autostart + Backups
Windows control plane (start/stop/status/chat/export), status `--json` contract, standardized errors, Scheduled Task autostart toggle, Backup Bundle backup/restore.
Goal: product-like operability for local use.

Key deliverables:
- **Stable entrypoint exists and is documented:**
  - `D:\Aetherforge\bin\aetherforge.ps1` delegates to `D:\Aetherforge\scripts\aether.ps1` and `scripts\commands\*.ps1`.
- Launcher implements lifecycle and operational commands with stable semantics:
  - `dev-core` (dev-mode Core bring-up)
  - `start` (shim to `dev-core` until M4; reserved for UI launch later)
  - `stop`
  - `status [--json]`
  - `export <conversation_id>`
  - `backup create` / `backup restore`
  - `autostart on|off`
- Launcher surfaces Core errors verbatim using the structured error model (`code/message/detail/hint`).
- `status --json` matches the `/v1/status` schema (core/ollama/pins/db/gpu/tailnet/files).
- Backup Bundle (cold backup) + deterministic restore procedure validated, including WSL DB snapshot rules (WAL/SHM inclusion or checkpoint-on-stop).

### M4 — Desktop-Native UI (MVP)
Windows-native UI (WPF recommended) layered over Core API; streaming chat (SSE), conversation list/search, exports, autostart toggle.
Goal: daily-driver UX. MVP is achieved at M4.

Key deliverables:
- UI uses Core API only (no direct DB access).
- Streaming chat view consumes SSE events (`meta/delta/done/error`).
- Conversation list/search + rename.
- Export button and autostart toggle.
- Role/tier selector for General/Coding.
- **`aether start` launches the Desktop UI** (no longer a `dev-core` shim).

### M5 — Tailnet Access (API + Remote CLI)
Tailnet exposure via Tailscale Serve on Windows as the single entrypoint; remote devices use CLI-only against the Core API.
Goal: seamless multi-device access to the same conversations (post-MVP).

Key deliverables:
- Windows hosts the tailnet entrypoint (Tailscale Serve) publishing the Core API.
- Tailnet membership is the auth boundary (no extra API auth).
- Remote CLI workflows documented + validated against the same Core conversation store.

Optional hardening (allowed in M5+, not required for M5 acceptance):
- Introduce an installed runtime path in WSL:
  - `/opt/aetherforge/<version>/` + optional `/opt/aetherforge/current` symlink
  - systemd services target the installed runtime rather than the repo workspace

### M6 — Agent Mode (Primary + Verifier, Safe Tools)
Tool-augmented reasoning with a strict two-phase plan→approval→act protocol, allowlists, and full audit logging.
Agent uses both `agent.primary` and `agent.verifier` pins: primary performs the task, verifier checks it.
Goal: controlled automation without expanding blast radius (post-MVP).

Key deliverables:
- Agent gate is enforced:
  - when `settings.yaml: agent.enabled=false`, Core rejects `role=agent` requests
- When Agent is enabled:
  - `pinned.yaml` requires both `agent.primary` and `agent.verifier` to be pinned (non-null digests)
- Agent pipeline:
  - PLAN emitted deterministically
  - explicit user approval required (per `agent.require_plan_approval`)
  - ACT executes only allowlisted tools
  - verifier step runs after primary output, and its result is recorded
- Full audit trail persisted (plan/approval/tool calls/tool results) and exported deterministically via `meta_json`.
- No arbitrary shell execution; filesystem reads/writes remain bounded by allowlists.
