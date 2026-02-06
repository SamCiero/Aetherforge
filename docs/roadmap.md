# Aetherforge — Project Roadmap

This roadmap is authoritative for sequencing work on Aetherforge.
Each milestone has a dedicated checklist file with explicit completion gates.

Progression rule: do not start a milestone until the previous one is complete.

## Canonical local URL + PowerShell quirk
- Canonical loopback host is `127.0.0.1` for all local calls (Windows→WSL and Windows-local).
- Do **not** use `localhost` (PowerShell/WinHTTP proxy settings can route it and add large latency).
  - Prefer `Invoke-RestMethod http://127.0.0.1:<port>/... -NoProxy` for local calls.
  - For raw timing probes, prefer `curl.exe http://127.0.0.1:<port>/...`.

## Role/tier glossary (per Spec)
- Roles: `general`, `coding`, `agent`
- General + Coding tiers: `fast`, `thinking`
- Agent mapping: `agent.primary` + `agent.verifier`
  - UX intent: “Agent” is a single mode selection; internally Core uses **primary** to do the work and **verifier** to verify it.

## Milestones

### M0 — Bootstrap Substrate
Foundation: WSL2, GPU passthrough, Ollama runtime, initial config scaffolding.
Goal: prove the platform assumptions.

Key deliverables (aligned with spec):
- WSL2 mirrored networking working.
- GPU visible in WSL (`nvidia-smi` evidence captured).
- Ollama installed and reachable at `http://127.0.0.1:11434`.
- Initial `settings.yaml` + `pinned.yaml` created (schema v1).
- At least one pinned, non-null digest for the fallback target (default `general.fast` unless configured otherwise).

### M1 — Core Backend + Persistence
Linux-side Core service with stable REST + SSE contract, settings validation, pin manifest loading/verification, SQLite policy, boundary enforcement, deterministic exports.
Goal: stable backend contract before any UI.

Key deliverables:
- Settings loader/validator for `settings.yaml` (schema v1), including:
  - `pins.mode` (`strict|fallback`) + `pins.fallback_role/tier`
  - boundary roots + allowlists + bridge rules (schema + validation)
- Pins loader/validator for `pinned.yaml` (schema v1) supporting:
  - null digests (unpinned placeholders)
  - optional `required` flag per entry
  - status-time evaluation: `pins_match` and `model_digests_match`
- Core API endpoints:
  - `GET /v1/status`
  - conversations CRUD subset: create, list/search, read, patch title
  - `POST /v1/chat` SSE streaming
  - `POST /v1/export/{id}` deterministic JSON + Markdown export
- SQLite operational policy (WAL, busy_timeout/retry rules, schema versioning/migrations, backup-copy rules).
- Boundary enforcement on exports (deny writes outside allowlisted roots; block reparse points if enabled).

### M2 — General + Coding Roles (Core Assistant)
Implement General + Coding with Fast/Thinking tiers, pinned role/tier mapping, fallback behavior, offline-capable operation, acceptance tested via CLI/API harness.
Goal: functional assistant baseline for daily evaluation via terminal.

Key deliverables:
- Conversation creation pins `{role, tier, model_tag, model_digest}` deterministically from `pinned.yaml`.
- Fallback behavior:
  - `strict`: missing/unpinned role/tier fails conversation creation
  - `fallback`: missing/unpinned role/tier resolves to `pins.fallback_role/tier`
  - Always truthful about the actual resolved model used
- SSE `meta` event includes `resolution` when fallback applies, formatted:
  - `fallback:<role>.<tier>`
- Offline validation gates: models pulled, digests match, chats succeed without network.
- Export includes model tag + digest and message `meta_json` where applicable.

### M3 — Windows Launcher + Autostart + Backups
Windows control plane (start/stop/status/chat/export), status `--json` contract, standardized errors, Scheduled Task autostart toggle, Backup Bundle backup/restore.
Goal: product-like operability for local use.

Key deliverables:
- `aetherforge.ps1` implements lifecycle commands and surfaces Core errors verbatim (code/message/detail/hint).
- `status --json` matches the `/v1/status` schema (core/ollama/pins/db/gpu/tailnet/files).
- Backup Bundle (cold backup) and deterministic restore procedure validated (including WSL DB snapshot rules with WAL/SHM or checkpoint-on-stop).

### M4 — Desktop-Native UI (MVP)
Windows-native UI (WPF recommended) layered over Core API; streaming chat (SSE), conversation list/search, exports, autostart toggle.
Goal: daily-driver UX. MVP is achieved at M4.

Key deliverables:
- UI uses Core API only (no direct DB access).
- Streaming chat view consumes SSE events (`meta/delta/done/error`).
- Conversation list/search + rename.
- Export button and autostart toggle.
- Role/tier selector for General/Coding (Agent mode UI can be stubbed/hidden until M6).

### M5 — Tailnet Access (API + Remote CLI)
Tailnet exposure via Tailscale Serve on Windows as the single entrypoint; remote devices use CLI-only against the Core API.
Goal: seamless multi-device access to the same conversations (post-MVP).

Key deliverables:
- Windows hosts the tailnet entrypoint (Tailscale Serve) publishing the Core API.
- Tailnet membership is the auth boundary (no extra API auth).
- Remote CLI workflows documented + validated against the same Core conversation store.

### M6 — Agent Mode (Primary + Verifier, Safe Tools)
Tool-augmented reasoning with a strict two-phase plan→approval→act protocol, allowlists, and full audit logging.
Agent uses both `agent.primary` and `agent.verifier` pins: primary performs the task, verifier checks it.
Goal: controlled automation without expanding blast radius (post-MVP).

Key deliverables:
- `pinned.yaml` requires both `agent.primary` and `agent.verifier` to be pinned (non-null digests) when Agent is enabled.
- Agent pipeline:
  - PLAN emitted deterministically
  - explicit user approval required (per `agent.require_plan_approval`)
  - ACT executes only allowlisted tools
  - verifier step runs after primary output, and its result is recorded
- Full audit trail persisted (plan/approval/tool calls/tool results) and exported deterministically via `meta_json`.
- No arbitrary shell execution; filesystem reads/writes remain bounded by allowlists.
