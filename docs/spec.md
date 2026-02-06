# Aetherforge (ChatGPT‑at‑Home) — Spec (WSL2 Linux internals, Windows host)

## 0. Product definition

Aetherforge is a local‑first AI assistant stack hosted on SAM‑DESKTOP (Windows 11) with a Linux‑based runtime layer inside WSL2. The system runs completely on the user’s machine and does not expose itself to the public internet.
It supports:
* General, Coding and Agent roles (Agent arrives in Phase 2)
* Fast and Thinking tiers for General and Coding roles; Primary and Verifier tiers for the Agent role
* Offline operation once models are pulled
* Windows desktop‑native UI layered over a CLI‑first backend

### 0.1 MVP definition (milestones)

* MVP is achieved at M4: Windows desktop‑native UI + product‑like operability.
* M2 is a CLI‑first core assistant milestone (useful baseline, not the MVP).
* M5 (tailnet + remote CLI) and M6 (agent tools) are post‑MVP expansions.

### 0.2 Non‑goals (MVP)

* Public internet exposure (no direct inbound from internet; CGNAT).
* Unrestricted tool execution (no arbitrary shell/git/fs writes without allowlists).
* Full UI on non‑desktop devices (remote devices are CLI‑only post‑MVP).

## 1. Authoritative decisions (from Sam)

These decisions represent the intended design direction and remain fixed unless explicitly revised.

### 1.1 Platform and execution

* D1: Execution model = WSL2 (Linux internals) with a Windows host.
* D2: Data source‑of‑truth = Hybrid:
  * Config, logs and exports live in Windows root D:\Aetherforge\…
* SQLite DB lives in WSL filesystem (for SQLite performance/locking reliability).
* D3: Model storage = WSL filesystem (fastest, avoids /mnt/* penalties).
* D4: Networking exposure = Tailscale‑first; LAN exposure optional later.
* D5: WSL networking mode = mirrored.
* D6: Autostart = Windows Scheduled Task (toggleable).
* D7: UI strategy = Windows desktop‑native UI (after CLI baseline).
* D8: Routing = manual selection ( role , tier ).
* D9: Agent tools = safe tools only (calculator, local search/index, time), strict allowlists.
* D10: Persistence = SQLite + YAML.
* D11: Backups = cold backup (stop services → copy).
* D12: Updates = pin everything (runtime + model digests); later can relax.
* D13: Tailnet exposure = Windows runs Tailscale Serve as the single tailnet entrypoint.
* D14: Tailnet security posture = tailnet membership is the auth boundary (no extra API auth).
* D15: Agent safety posture = plan‑then‑act with explicit user approval before any tool runs.

## 2. Target environment

### 2.1 Hosts

* Windows host: SAM‑DESKTOP
* Future gateway: SAM‑PI5 (LAN IP: 192.168.40.73 )
(Exact IP inventory is sourced from context.NetworkSpecs.yaml when present.)

### 2.2 Networking constraints

* ISP uses CGNAT, so no port‑forwarding assumptions.
* Primary remote access method (post‑MVP): Tailscale tailnet.

## 3. High‑level architecture

### 3.1 Components

1) WSL2 distro (Ubuntu recommended) 2) Ollama (Linux) in WSL2 — model runtime + HTTP API (WSL‑local) 3) Aetherforge Core (Linux service) — chat orchestration, persistence, exports, tools 4) Aetherforge Windows Launcher (PowerShell) — lifecycle + CLI chat + status + backup/restore 5) Aetherforge Desktop UI (Windows) — native GUI consuming the Core API 6) Tailscale Serve (Windows, post‑MVP) — expose Core API to tailnet only

### 3.2 Process layout (steady state)

* WSL2:
  * ollama serve bound to 127.0.0.1:11434 inside WSL
* aetherforge-core bound to 127.0.0.1:8484 inside WSL
* Windows:
  * Launcher + UI call Core via Windows loopback at http://127.0.0.1:8484 (canonical)
* Post‑MVP: Tailscale Serve can publish the same Windows loopback endpoint to the tailnet

### 3.3 Trust boundaries

* Boundary A (Tool execution): tool calls validated by Core and restricted to allowlists.
* Boundary B (Network): no public exposure; tailnet‑only access (post‑MVP) via Windows Serve.
* Boundary C (Storage): DB in WSL filesystem; Windows holds configs/exports; no writes outside
allowlisted paths.

## 4. Filesystem layout

### 4.1 Windows root (human‑editable + artifacts)

D:\Aetherforge\ * config\settings.yaml (global settings) * config\pinned.yaml (pin manifest) * config\profiles\general.yaml * config\profiles\coding.yaml * config\profiles\agent.yaml * logs\bootstrap\status.json (M0 snapshot; tracked even though logs are otherwise ignored) * exports\ (conversation exports, backup bundles) * bin\ (Windows launcher + UI binaries) * aetherforge.ps1 * aetherforge.cmd * aetherforge-ui.exe (M4+)

### 4.2 WSL filesystem (performance‑sensitive runtime data)

* /opt/aetherforge/ (service code/runtime)
* /var/lib/aetherforge/
* conversations.sqlite
* indexes/ (local‑search index; Phase 2)
* /var/lib/ollama/ (models; canonical model store)

## 5. Model suite and role/tier mapping

### 5.1 Required models (pins)

Pins map each {role, tier} to a model tag. A digest is recorded in pinned.yaml once the model is pulled. When running in strict mode, every role/tier must have a non‑null digest pinned; missing digests cause conversation creation to fail. In fallback mode (see §5.2), only the configured fallback role/tier must have a pinned digest.
* General:
  * Fast: qwen2.5:7b‑instruct
* Thinking: qwen2.5:14b‑instruct
* Coding:
  * Fast: qwen2.5-coder:7b‑instruct
* Thinking: qwen2.5-coder:14b
* Agent:
  * Primary: gpt-oss:20b
* Verifier: gpt-oss-safeguard:20b
Note: In Agent mode, two models are involved: a primary model that performs the task and a verifier model that checks the primary’s output for safety or correctness. Both must be pulled and pinned in pinned.yaml when agent mode is enabled.

### 5.2 Routing and fallback rules

* Manual role/tier selection: The user chooses {role, tier} explicitly (UI selector; CLI flags).
Each conversation records the chosen role and tier along with the resolved model tag and digest at creation time. Changing the role or tier mid‑conversation starts a new conversation.
* Per‑conversation pinning: On conversation creation, Core consults pinned.yaml and pins the
resolved {model_tag, model_digest} to the conversation. This ensures deterministic exports and replay.
* Fallback policy: Core reads pins.mode from settings.yaml . In strict mode, every
requested role/tier must exist in pinned.yaml with a non‑null digest; otherwise conversation creation fails. In fallback mode, if the requested role/tier is missing or unpinned (null digest), Core looks up pins.fallback_role and pins.fallback_tier from settings.yaml and uses that entry’s tag and digest while preserving the user’s requested role and tier in the conversation metadata. The fallback role/tier must exist in pinned.yaml with a non‑null digest.
Core includes a resolution hint (see §10.3) when a fallback is applied.
* For the Agent role, pins.fallback_tier should be either primary or verifier .

### 5.3 Tier semantics

* Fast: lower latency parameters and lower reasoning verbosity (or disabled where supported).
* Thinking: higher reasoning settings where supported; lower temperature (more determinism) for
coding.
* Primary: for the Agent role, the model that carries out the main task.
* Verifier: for the Agent role, the model used to inspect and verify the primary’s output; emphasises
safety and correctness.

## 6. Networking and access (local‑first; tailnet post‑MVP)

### 6.1 Canonical local endpoint (M0–M4)

Canonical Core base URL for Windows clients is: http://127.0.0.1:8484 .
Do not use http://localhost:8484 for anything performance‑sensitive. In this environment, localhost can be routed through Windows proxy/WPAD behaviour and become much slower. Using 127.0.0.1 avoids the proxy and is consistently fast for PowerShell, curl and the UI.

### 6.2 Windows↔WSL reachability gate (M1)

M1 must prove the Windows host can call the Core API reliably.
* Preferred: mirrored networking is sufficient (Windows loopback reaches WSL‑bound Core).
* Fallback: Windows reverse‑proxy/bridge to WSL Core (documented and testable).

### 6.3 Tailnet sharing (post‑MVP; M5)

* Tailscale Serve runs on Windows as the single tailnet entrypoint.
* Serve publishes the same Core API endpoint to the tailnet.
* Funnel stays off.

### 6.4 Tailnet auth posture

* Tailnet membership is the authentication boundary.
* No additional API auth is required (post‑MVP).

## 7. Runtime management, pinning, status, backups

### 7.1 Start/stop behaviour

Start: 1) Ensure WSL is running. 2) Start Ollama in WSL. 3) Start Aetherforge Core in WSL. 4) Optionally start UI on Windows.
Stop: 1) Stop UI. 2) Stop Core. 3) Stop Ollama.

### 7.2 Autostart

Autostart is implemented via a Windows Scheduled Task (on logon). It can be toggled via the UI (M4) or via aetherforge.ps1 --autostart on|off .

### 7.3 Pinning and update policy

Pins are recorded at D:\Aetherforge\config\pinned.yaml . A pin manifest defines which model tag and digest should be used for each role/tier. Model digests come from Ollama’s /api/tags and should be normalised to lowercase 64‑character hex without a sha256: prefix.

#### 7.3.1 pinned.yaml schema (v1)

Required top‑level fields:
* schema_version: 1
* captured_utc: <ISO‑8601> — when the manifest was generated.
* ollama:
  * version: <string> — version of Ollama when the manifest was generated.
* models: mapping (role → tier → entry).
Each model entry includes:
* tag: <string> — the Ollama tag to load.
* digest: <64‑hex‑lowercase | null> — the model’s digest; null indicates the role/tier is
unpinned and will trigger fallback in fallback mode.
* required: <bool> (optional) — whether the digest must be provided. If required is true
and digest is null or invalid, the manifest is considered incomplete and the status API reports pins_match = false .

Example:

```yaml
schema_version: 1
captured_utc: "2026-01-15T00:00:00Z"
ollama:
  version: "<OLLAMA_VERSION>"
models:
  general:
    fast:
      tag: "qwen2.5:7b-instruct"
      digest: "845dbda0ea48ed749caafd9e6037047aa19acfcfd82e704d7ca97d631a0b697e"
      required: true
    thinking:
      tag: "qwen2.5:14b-instruct"
      digest: null
      required: false
  coding:
    fast:
      tag: "qwen2.5-coder:7b-instruct"
      digest: null
      required: false
    thinking:
      tag: "qwen2.5-coder:14b"
      digest: null
      required: false
  agent:
    primary:
      tag: "gpt-oss:20b"
      digest: null
      required: true
    verifier:
      tag: "gpt-oss-safeguard:20b"
      digest: null
      required: true
```

#### 7.3.2 Upgrade behaviour

Upgrades are explicit. To upgrade a model:
1) Pull the new model in Ollama. 2) Update pinned.yaml with the new tag and digest. 3) Write a dated backup copy of the old manifest alongside it (for rollback).

### 7.4 status contract

aetherforge.ps1 status provides human output and a --json mode. The /v1/status API returns the same information in machine‑readable form.

#### 7.4.1 Stable JSON keys (minimum)

* schema_version — response schema version (always 1 for MVP).
* captured_utc — when the status snapshot was generated.
* core:
  * reachable (bool) — whether the Core API responded.
* version (string) — Core assembly version or empty string if unavailable.
* base_url (string) — canonical Core base URL.
* ollama:
  * reachable (bool) — whether Ollama responded.
* version (string or null) — Ollama version.
* models_dir (string or null) — canonical models directory (e.g. /var/lib/ollama ).
* pins:
  * pinned_yaml_path (string) — path to pinned.yaml .
* pins_match (bool or null) — true if the manifest contains entries for all required role/tier
combinations and all required digests are present; null if the manifest cannot be read.
* model_digests_match (bool or null) — true if all pinned digests match the live digests
reported by Ollama; null if live tags cannot be fetched or manifest is missing.
* detail (string or null) — human‑readable description of any manifest issues.
* db:
  * path (string) — path to SQLite database.
* healthy (bool) — whether the DB schema is present and versioned correctly.
* error (string or null) — error message if unhealthy.
* gpu:
  * visible (bool) — whether a GPU is visible to the runtime.
* evidence (string or null) — deterministic evidence (e.g. output of nvidia‑smi ).
* tailnet:
  * serve_enabled (bool) — whether Tailscale Serve is enabled.
* published_port (int or null) — port exposed via Serve (post‑MVP).
* files:
  * settings_exists (bool) — whether settings.yaml exists on disk.
* pinned_exists (bool) — whether pinned.yaml exists on disk.

### 7.5 Backup bundle

Backups are cold backups.

#### 7.5.1 Bundle contents

A single zip bundle includes:
* Windows tree snapshot: D:\Aetherforge\… (config, logs, exports, bin as configured).
* WSL DB snapshot copied into the bundle:
  * source: /var/lib/aetherforge/conversations.sqlite plus any required SQLite sidecars per policy.

#### 7.5.2 Deterministic restore procedure (minimum)

1) Stop services (UI/Core/Ollama). 2) Restore Windows tree snapshot. 3) Restore WSL DB snapshot to /var/ lib/aetherforge/ . 4) Start services. 5) Validate: * conversations list returns expected items * pins match
* export works

## 8. Persistence

### 8.1 SQLite schema (MVP)

DB location: /var/lib/aetherforge/conversations.sqlite .
Tables: * conversations * id INTEGER PK * created_utc TEXT * role TEXT ( general | coding | agent ) * tier TEXT — tier name, which depends on the role. For General and Coding roles the tiers are fast and thinking ; for the Agent role the tiers are primary and verifier . * model_tag TEXT * model_digest TEXT * title TEXT * messages * id INTEGER PK * conversation_id INTEGER FK * created_utc TEXT * sender TEXT ( user | assistant | tool | system ) * content TEXT * meta_json TEXT (optional; SSE metadata, tool events, token counts)

### 8.2 SQLite operational policy

* WAL mode is enabled (and verified).
* busy_timeout is set and retry rules are defined in Core.
* Schema versioning/migrations are required (minimal mechanism is sufficient).
* Backup rules:
  * If WAL is enabled, backup must either include WAL/SHM files or enforce checkpoint‑on‑stop before copying.
Note (WSL + scripting reality): when initializing the DB from Windows via wsl.exe -- bash -lc <script> , CRLF and quoting can corrupt multi‑line SQL/heredocs. See §15 (Quirks).

### 8.3 YAML config (MVP)

D:\Aetherforge\config\settings.yaml holds runtime settings. Required keys:
* schema_version: integer (should be 1).
* captured_utc: ISO‑8601 timestamp or null.
* ports:
  * core_bind_url — absolute HTTP URL for the Core API (must use host 127.0.0.1 ).
* ollama_base_url — absolute HTTP URL for the Ollama API (must use host 127.0.0.1 ).
* defaults:
  * role — default role ( general | coding | agent ).
* tier — default tier ( fast | thinking ).
* pins:
  * mode — strict or fallback . See §5.2.
* fallback_role — role to use in fallback mode when requested role/tier is unpinned.
* fallback_tier — tier to use in fallback mode when requested role/tier is unpinned.
* profiles:
  * root_wsl — WSL path containing system prompt files.
* by_role — map of role name → filename (e.g. general: "general.yaml" ).
* generation:
  * by_profile — nested map of role → tier → parameter values; each value is an object with optional temperature , top_p , top_k , num_ctx , num_predict , repeat_penalty and seed . Null values indicate defaults should be used.
* autostart:
  * enabled — bool.
* windows_scheduled_task_name — name of the scheduled task or null.
* boundary:
  * bridge_rules — list of objects mapping Windows paths ( windows_root ) to WSL paths ( wsl_root ). Only explicitly mapped prefixes are permitted; unknown mappings are denied.
* roots:
◦ wsl.config — absolute WSL path for configuration files (e.g. /mnt/d/Aetherforge/ config ).
◦ wsl.exports — absolute WSL path for exports directory.
◦ wsl.logs — absolute WSL path for logs directory.
  * block_reparse_points — bool; when true, deny writes through reparse points/symlinks.
* allow_write_under_wsl — list of WSL paths under which the Core is allowed to write. Must
include the config, exports and logs roots.
* allow_read_under_wsl — list of WSL paths that may be read (optional; empty by default).
* agent:
  * enabled — bool.
* require_plan_approval — bool; if true, agent tool plans must be explicitly approved by the user.
* allow_tools — list of allowed tool names (empty list means no tools are permitted).

## 9. Filesystem boundary policy (allowlists)

Boundary policy is configured via settings.yaml . Minimum required configuration:
* Allowlisted roots for writes: the WSL paths for config, exports and logs must be present in
allow_write_under_wsl . Additional directories may be added as needed. All writes are canonicalised and denied if the target is outside these roots.
* Allowlisted roots for reads (Phase 2 local_search): explicit list in allow_read_under_wsl
(empty by default).
* Enforcement rules:
  * Canonicalise every path before access.
* Deny if the target is outside allowlisted roots.
* Deny through reparse points/symlinks/junctions when block_reparse_points is true.
* Apply Windows↔WSL bridge rules; deny unknown mappings.
Environment overrides: The exports root can be overridden at runtime via the AETHERFORGE_EXPORTS_ROOT environment variable. If this is used, operators must update allow_write_under_wsl accordingly so that boundary enforcement permits writing into the new exports directory.

## 10. Core API

### 10.1 Error model

All error responses are structured with the following properties:
* code — stable machine‑readable code (e.g. BAD_REQUEST , PIN_MISSING ).
* message — human‑readable summary.
* detail — optional technical detail.
* hint — optional actionable hint for remediation.

### 10.2 REST endpoints (Phase 1)

* GET /v1/status — return system status including core, ollama, pins, db, gpu, tailnet and file
information (see §7.4).
* POST /v1/conversations — create a new conversation, pinning role/tier/model. Validates the
requested role/tier and applies the fallback policy if configured.
* GET /v1/conversations/{id} — retrieve a conversation and its messages.
* GET /v1/conversations?limit=&offset=&q= — list conversations with optional search (by
title substring).
* PATCH /v1/conversations/{id} — update conversation metadata (currently only the title).
* POST /v1/chat — send a message and stream assistant responses via Server‑Sent Events (see
§10.3).
* POST /v1/export/{id} — write a Markdown + JSON export under the exports root.

### 10.3 Streaming protocol ( POST /v1/chat ) — SSE

Transport: Server‑Sent Events. The server sends a series of events as lines of event: and data: fields separated by blank lines.
Event types:
* meta — sent once at the beginning. Includes:
  * conversation_id — integer.
* message_id — integer (assistant placeholder ID).
* model_tag — string.
* model_digest — string.
* resolution — string or null; present when the server used the fallback policy. Format:
"fallback:<role>.<tier>" .
  * delta — sent zero or more times. Includes:
  * message_id — integer.
* delta_text — incremental assistant text.
* done — sent once at the end. Includes:
  * message_id — integer.
* error — sent if an error occurs mid‑stream. The payload is a structured error object as defined in
§10.1.

### 10.4 Export schema

Exports are written under D:\Aetherforge\exports\YYYY‑MM‑DD\ . For each conversation, two files are created:
* <conversation_id>.<title_slug>.md — Markdown transcript with metadata header
(conversation id/title/role/tier/model tag + digest) followed by a chronological message transcript.
* <conversation_id>.<title_slug>.json — JSON export.

#### 10.4.1 JSON export schema (v1)

Top‑level required fields:
* schema_version: 1
* generated_utc: <ISO‑8601>
* core_version: <string> or "unknown"
* conversation: — object with id , created_utc , title , role , tier .
* model: — object with tag and digest .
* messages: — array of objects with id , created_utc , sender , content , meta_json
(optional).
Markdown conventions: * A header lists the conversation metadata (created_utc, role, tier, model tag and digest). * Messages are listed chronologically with headings indicating the sender and timestamp.

## 11. Agent tools (Phase 2) — safe tools only

Allowed tool classes: * calculator — pure evaluation; no side effects. * local_search — read‑only search over allowlisted directories. * time — reading the system time.
Hard rules: * No arbitrary shell commands. * No filesystem writes except under allowlisted roots. * Every tool call is logged as an auditable event (DB + audit log).

### 11.1 Plan‑then‑act protocol (strict)

* PLAN: produce a structured plan object (tools, arguments, allowlist targets, expected outputs, risks).
* APPROVAL: require explicit user approval before any tool executes. The UI/CLI must present the plan
to the user.
* ACT: execute the approved tool calls; record results.
Audit requirements: * Persist the plan, approval action, tool calls and tool results as events (DB + audit log).
* Represent these events deterministically in messages.meta_json and in exports.

## 12. Implementation plan (milestones)

(See AetherRoadmap.md and checklist files; these are authoritative for sequencing and gates.)

### 12.1 M0 — Bootstrap substrate

* WSL2 + mirrored networking.
* NVIDIA GPU visible in WSL.
* Ollama installed.
* Initial pin manifest created.
* Bootstrap status snapshot captured and tracked.

### 12.2 M1 — Core backend + persistence

* Core REST contract (incl. list/search + title update).
* SSE streaming.
* SQLite operational policy + migration mechanism.
* Boundary enforcement.
* Export schema.
* Windows↔WSL reachability proof + fallback doc.

### 12.3 M2 — General + coding roles (core assistant)

* Role/tier mapping from pinned.yaml .
* Per‑conversation pinning.
* Offline validation.

### 12.4 M3 — Windows launcher + autostart + backups

* Lifecycle + status --json.
* Standardised errors.
* Backup Bundle backup/restore.

### 12.5 M4 — Desktop‑native UI (MVP)

* WPF UI layered over Core API.
* Streaming chat (SSE).
* Conversation list/search + title edit.
* Export + autostart toggle.

### 12.6 M5 — Tailnet access (post‑MVP)

* Windows Tailscale Serve publishes API to tailnet.
* Remote devices use CLI‑only against API.

### 12.7 M6 — Agent mode (post‑MVP)

* Safe tools + allowlists.
* Strict plan→approval→act protocol.

## 13. Risks and mitigations (WSL2‑specific)

* R1: Windows↔WSL networking quirks.
* Mitigation: mirrored networking; explicit reachability gate; bridge/proxy fallback.
* R2: SQLite performance on /mnt/* .
* Mitigation: DB lives in WSL filesystem.
* R3: GPU not used in WSL.
* Mitigation: verify nvidia-smi ; capture deterministic GPU evidence; expose via status --
json .
* R4: Windows localhost proxy slowness.
* Mitigation: canonicalise to 127.0.0.1 and explicitly avoid localhost for API calls.

## 14. Definition of Done (MVP = M4)

MVP is done when:
* Desktop UI supports General + Coding roles in Fast/Thinking (manual role/tier selection).
* Conversations persist across reboot.
* Exports work (Markdown + JSON with schema_version + model tag + digest).
* Autostart toggle works.
* Offline operation verified.
Post‑MVP (separate milestones): * M5: Tailnet API + remote CLI access. * M6: Agent tools.

## 15. Quirks and operational gotchas (must‑follow)

### 15.1 Canonical URL (Windows)

* Always prefer http://127.0.0.1:8484 (Core) and http://127.0.0.1:11434 (Ollama).
* Avoid localhost unless you explicitly force no proxy behaviour and have measured it.

### 15.2 PowerShell vs Bash context

* systemctl and sudo are Linux/WSL commands; they only work under:
  * wsl.exe -- bash -lc '<command>'
* Do not paste C# code into PowerShell; edit Program.cs (or other source files) and build.

### 15.3 WSL command passing pitfalls

When passing multi‑line scripts into wsl.exe -- bash -lc $cmd : * Strip CRLF before execution (PowerShell heredocs often include \r ). * Prefer single‑quoted heredoc delimiters on the Bash side ( <<'SQL' ) to prevent interpolation. * When deleting *-wal / *-shm , always use rm -f -- ... (the
-- prevents dash‑prefixed mis‑parsing).

### 15.4 SQLite WAL verification nuance

* WAL/SHM sidecars may not appear until there is write activity; verification should use SQLite
pragmas and a simple write in the same connection.
