# Aetherforge (ChatGPT-at-Home) — Spec (WSL2 Linux internals, Windows host)

## 0. Product definition
Aetherforge is a **local-first AI assistant stack** hosted on **SAM-DESKTOP** (Windows 11) with a **Linux-based runtime layer** inside **WSL2**.

It supports:
- **General** and **Coding** modes (Phase 1)
- **Agent (tools)** mode (Phase 2)
- **Fast** and **Thinking** tiers per mode
- **Offline operation** once models are pulled
- **Windows desktop-native UI** layered over a CLI-first backend

### 0.1 MVP definition (milestones)
- **MVP is achieved at M4**: Windows desktop-native UI + product-like operability.
- M2 is a **CLI-first core assistant** milestone (useful baseline, not the MVP).
- M5 (tailnet + remote CLI) and M6 (agent tools) are **post-MVP** expansions.

### 0.2 Non-goals (MVP)
- Public internet exposure (no direct inbound from internet; CGNAT).
- Unrestricted tool execution (no arbitrary shell/git/fs writes without allowlists).
- Full UI on non-desktop devices (remote devices are CLI-only post-MVP).

---

## 1. Authoritative decisions (from Sam)
These are pinned unless explicitly revised.

### 1.1 Platform and execution
- D1: Execution model = **WSL2** (Linux internals), Windows host.
- D2: Data source-of-truth = **Hybrid**:
  - Config/logs/exports live in **Windows root** `D:\Aetherforge\...`
  - SQLite DB lives in **WSL filesystem** (for SQLite performance/locking reliability)
- D3: Model storage = **WSL filesystem** (fastest, avoids `/mnt/*` penalties)
- D4: Networking exposure = **Tailscale-first**; LAN exposure optional later
- D5: WSL networking mode = **mirrored**
- D6: Autostart = **Windows Scheduled Task** (toggleable)
- D7: UI strategy = **Windows desktop-native UI** (after CLI baseline)
- D8: Routing = **manual selection** (`role`, `tier`)
- D9: Agent tools = **safe tools only** (calculator, local search/index, time), strict allowlists
- D10: Persistence = **SQLite + YAML**
- D11: Backups = **cold backup** (stop services → copy)
- D12: Updates = **pin everything** (runtime + model digests); later can relax
- D13: Tailnet exposure = **Windows runs Tailscale Serve** as the single tailnet entrypoint
- D14: Tailnet security posture = **tailnet membership is the auth boundary** (no extra API auth)
- D15: Agent safety posture = **plan-then-act with explicit user approval** before any tool runs

---

## 2. Target environment

### 2.1 Hosts
- Windows host: **SAM-DESKTOP**
- Future gateway: **SAM-PI5** (LAN IP: `192.168.40.73`)

(Exact IP inventory is sourced from `context.NetworkSpecs.yaml` when present.)

### 2.2 Networking constraints
- ISP uses CGNAT, so **no port-forwarding assumptions**.
- Primary remote access method (post-MVP): **Tailscale tailnet**.

---

## 3. High-level architecture

### 3.1 Components
1) **WSL2 distro** (Ubuntu recommended)
2) **Ollama (Linux) in WSL2** — model runtime + HTTP API (WSL-local)
3) **Aetherforge Core (Linux service)** — chat orchestration, persistence, exports, tools
4) **Aetherforge Windows Launcher (PowerShell)** — lifecycle + CLI chat + status + backup/restore
5) **Aetherforge Desktop UI (Windows)** — native GUI consuming the Core API
6) **Tailscale Serve (Windows, post-MVP)** — expose Core API to tailnet only

### 3.2 Process layout (steady state)
- WSL2:
  - `ollama serve` bound to `127.0.0.1:11434` inside WSL
  - `aetherforge-core` bound to `127.0.0.1:8484` inside WSL
- Windows:
  - Launcher + UI call Core via **Windows loopback** at **`http://127.0.0.1:8484`** (canonical)
  - Post-MVP: Tailscale Serve can publish the same Windows loopback endpoint to the tailnet

### 3.3 Trust boundaries
- **Boundary A (Tool execution):** tool calls validated by Core and restricted to allowlists.
- **Boundary B (Network):** no public exposure; tailnet-only access (post-MVP) via Windows Serve.
- **Boundary C (Storage):** DB in WSL filesystem; Windows holds configs/exports; no writes outside allowlisted paths.

---

## 4. Filesystem layout

### 4.1 Windows root (human-editable + artifacts)
`D:\Aetherforge\`
- `config\settings.yaml` (global settings)
- `config\pinned.yaml` (pin manifest)
- `config\profiles\general.yaml`
- `config\profiles\coding.yaml`
- `config\profiles\agent.yaml`
- `logs\bootstrap\status.json` (M0 snapshot; tracked even though logs are otherwise ignored)
- `exports\` (conversation exports, backup bundles)
- `bin\` (Windows launcher + UI binaries)
  - `aetherforge.ps1`
  - `aetherforge.cmd`
  - `aetherforge-ui.exe` (M4+)

### 4.2 WSL filesystem (performance-sensitive runtime data)
- `/opt/aetherforge/` (service code/runtime)
- `/var/lib/aetherforge/`
  - `conversations.sqlite`
  - `indexes/` (local-search index; Phase 2)
- `/var/lib/ollama/` (models; canonical model store)

---

## 5. Model suite and role/tier mapping

### 5.1 Required models (pins)
- General:
  - Fast: `qwen2.5:7b-instruct`
  - Thinking: `qwen2.5:14b-instruct`
- Coding:
  - Fast: `qwen2.5-coder:7b-instruct`
  - Thinking: `qwen2.5-coder:14b`
- Agent (Phase 2):
  - Primary: `gpt-oss:20b`
  - Optional verifier: `gpt-oss-safeguard:20b`

### 5.2 Routing rules (manual)
- User chooses `{role, tier}` explicitly (UI selector; CLI flags).
- Each conversation pins `{role, tier, model_tag, model_digest}` at creation time.
- Mid-conversation switching starts a **new conversation** by default.

### 5.3 Tier semantics
- Fast:
  - Lower latency parameters
  - Lower reasoning verbosity (or disabled where supported)
- Thinking:
  - Higher reasoning settings where supported
  - Lower temperature (more determinism) for coding

---

## 6. Networking and access (local-first; tailnet post-MVP)

### 6.1 Canonical local endpoint (M0–M4)
**Canonical Core base URL for Windows clients is:**
- `http://127.0.0.1:8484`

**Do not use** `http://localhost:8484` for anything performance-sensitive:
- In this environment, `localhost` can be routed through Windows proxy/WPAD behavior and become ~20s slower for PowerShell `Invoke-RestMethod` / `Invoke-WebRequest`.
- Using `127.0.0.1` is consistently fast for `curl.exe` and PowerShell when paired with `-NoProxy` where applicable.

### 6.2 Windows↔WSL reachability gate (M1)
M1 must prove the Windows host can call the Core API reliably.
- Preferred: mirrored networking is sufficient (Windows loopback reaches WSL-bound Core).
- Fallback: Windows reverse-proxy/bridge to WSL Core (documented and testable).

### 6.3 Tailnet sharing (post-MVP; M5)
- **Tailscale Serve runs on Windows** as the single tailnet entrypoint.
- Serve publishes the same Core API endpoint to the tailnet.
- Funnel stays off.

### 6.4 Tailnet auth posture
- Tailnet membership is the authentication boundary.
- No additional API auth is required (post-MVP).

---

## 7. Runtime management, pinning, status, backups

### 7.1 Start/stop behavior
- Start:
  1) Ensure WSL is running
  2) Start Ollama in WSL
  3) Start Aetherforge Core in WSL
  4) Optionally start UI on Windows
- Stop:
  1) Stop UI
  2) Stop Core
  3) Stop Ollama

### 7.2 Autostart
- Implement via Windows Scheduled Task (On logon).
- Toggle via:
  - UI settings switch (M4)
  - `aetherforge.ps1 --autostart on|off`

### 7.3 Pinning and update policy
Pins are recorded at:
- `D:\Aetherforge\config\pinned.yaml`

#### 7.3.1 `pinned.yaml` schema (v1)
Required fields (minimum):
- `schema_version: 1`
- `captured_utc: <ISO-8601>`
- `ollama:`
  - `version: <string>`
- `models:` mapping (role/tier → model)
  - each entry includes:
    - `tag: <string>`
    - `digest: <64-hex-lowercase>` (matches Ollama `/api/tags` `digest`)

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
    thinking:
      tag: "qwen2.5:14b-instruct"
      digest: "<DIGEST>"
  coding:
    fast:
      tag: "qwen2.5-coder:7b-instruct"
      digest: "<DIGEST>"
    thinking:
      tag: "qwen2.5-coder:14b"
      digest: "<DIGEST>"
  agent:
    primary:
      tag: "gpt-oss:20b"
      digest: "<DIGEST>"
    verifier:
      tag: "gpt-oss-safeguard:20b"
      digest: "<DIGEST>"
```

#### 7.3.2 Upgrade behavior
- Upgrades are explicit.
- Upgrading pins must:
  - update `pinned.yaml`
  - write a dated backup copy alongside it (rollback)

### 7.4 `status` contract
`aetherforge.ps1 status` provides human output and `--json` mode.

#### 7.4.1 Stable JSON keys (minimum)
- `schema_version`
- `captured_utc`
- `core:`
  - `reachable` (bool)
  - `base_url` (canonical: `http://127.0.0.1:8484`)
- `ollama:`
  - `reachable` (bool)
  - `version` (string or null)
  - `models_dir` (string or null; canonical: `/var/lib/ollama`)
- `pins:`
  - `pinned_yaml_path`
  - `pins_match` (bool or null)
  - `model_digests_match` (bool or null)
- `db:`
  - `path` (string)
  - `healthy` (bool)
  - `error` (string or null)
- `gpu:`
  - `visible` (bool)
  - `evidence` (string; deterministic evidence description)
- `tailnet:`
  - `serve_enabled` (bool)
  - `published_port` (int or null)
- `files:`
  - `settings_exists` (bool)
  - `pinned_exists` (bool)

### 7.5 Backup Bundle
Backups are cold backups.

#### 7.5.1 Bundle contents
A single zip bundle includes:
- Windows tree snapshot: `D:\Aetherforge\...` (config/logs/exports/bin as configured)
- WSL DB snapshot copied into the bundle:
  - source: `/var/lib/aetherforge/conversations.sqlite` plus any required SQLite sidecars per policy

#### 7.5.2 Deterministic restore procedure (minimum)
1) Stop services (UI/Core/Ollama)
2) Restore Windows tree snapshot
3) Restore WSL DB snapshot to `/var/lib/aetherforge/`
4) Start services
5) Validate:
   - conversations list returns expected items
   - pins match
   - export works

---

## 8. Persistence

### 8.1 SQLite schema (MVP)
DB location: `/var/lib/aetherforge/conversations.sqlite`

Tables:
- `conversations`
  - `id` INTEGER PK
  - `created_utc` TEXT
  - `role` TEXT (general|coding|agent)
  - `tier` TEXT (fast|thinking)
  - `model_tag` TEXT
  - `model_digest` TEXT
  - `title` TEXT
- `messages`
  - `id` INTEGER PK
  - `conversation_id` INTEGER FK
  - `created_utc` TEXT
  - `sender` TEXT (user|assistant|tool|system)
  - `content` TEXT
  - `meta_json` TEXT (optional; SSE metadata, tool events, token counts)

### 8.2 SQLite operational policy
- WAL mode enabled (and verified).
- busy_timeout set and retry rules defined in Core.
- Schema versioning/migrations required (minimal mechanism is sufficient).
- Backup rules:
  - If WAL is enabled, backup must either include WAL/SHM files OR enforce checkpoint-on-stop before copying.

**Note (WSL + scripting reality):** when initializing the DB from Windows via `wsl.exe -- bash -lc <script>`,
CRLF and quoting can corrupt multi-line SQL/heredocs. See §15 (Quirks).

### 8.3 YAML config (MVP)
`D:\Aetherforge\config\settings.yaml` includes:
- defaults (role/tier)
- generation parameters per role/tier
- ports/binds
- autostart flag
- boundary allowlists
- agent safety flags (Phase 2)

Profiles:
- system prompts per role in `config\profiles\*.yaml`

---

## 9. Filesystem boundary policy (allowlists)
Boundary policy lives in `settings.yaml`.

Minimum required configuration:
- allowlisted roots for writes:
  - config root
  - exports root
  - logs root
- allowlisted roots for reads (Phase 2 local_search):
  - explicit list (empty by default)

Enforcement rules:
- Canonicalize every path before access.
- Deny if target is outside allowlisted roots.
- Deny reparse points/symlinks/junctions by default.
- WSL↔Windows bridge rules are explicit; deny unknown mappings.

---

## 10. Core API

### 10.1 Error model
All error responses are structured:
- `code` (stable)
- `message` (human)
- `detail` (optional)
- `hint` (actionable)

### 10.2 REST endpoints (Phase 1)
- `POST /v1/conversations` — create conversation (pins role/tier/model)
- `GET /v1/conversations/{id}` — read conversation + messages
- `GET /v1/conversations?limit=&offset=&q=` — list (and search if `q` provided)
- `PATCH /v1/conversations/{id}` — update metadata (title)
- `POST /v1/chat` — streaming chat via SSE
- `POST /v1/export/{id}` — write export bundle (Markdown + JSON) under exports root

### 10.3 Streaming protocol (`POST /v1/chat`) — SSE
Transport: Server-Sent Events.

Event naming:
- `event: meta` — initial metadata (conversation_id, message_id, pinned model)
- `event: delta` — incremental assistant text
- `event: done` — completion marker
- `event: error` — structured error object

Each event includes a single JSON payload in `data:`.

Minimum payload shapes:
- `meta`:
  - `conversation_id`
  - `message_id`
  - `model_tag`
  - `model_digest`
- `delta`:
  - `message_id`
  - `delta_text`
- `done`:
  - `message_id`
- `error`:
  - `code/message/detail/hint`

### 10.4 Export schema
Exports are written under:
- `D:\Aetherforge\exports\YYYY-MM-DD\`

Files per export:
- `.../<conversation_id>.<title_slug>.md`
- `.../<conversation_id>.<title_slug>.json`

#### 10.4.1 JSON export schema (v1)
Top-level required fields:
- `schema_version: 1`
- `generated_utc`
- `core_version` (string or "unknown")
- `conversation` (id, created_utc, title, role, tier)
- `model` (tag, digest)
- `messages[]` (id, created_utc, sender, content, meta_json)

Markdown conventions:
- Metadata header (conversation id/title/role/tier/model tag+digest)
- Chronological message transcript

---

## 11. Agent tools (Phase 2) — safe tools only
Allowed tool classes:
- `calculator` (pure; no side effects)
- `local_search` (read-only; allowlisted directories)
- `time` (system time)

Hard rules:
- No arbitrary shell commands
- No filesystem writes except under allowlisted roots
- Every tool call is logged as an auditable event (DB + audit log)

### 11.1 Plan-then-act protocol (strict)
- PLAN: produce a structured plan object (tools, args, allowlist targets, expected outputs, risks)
- APPROVAL: require explicit user approval before any tool executes
- ACT: execute tool calls; record results

Audit requirements:
- Persist plan, approval, tool calls, tool results as events (DB + audit log)
- Represent these events deterministically in `messages.meta_json` and exports

---

## 12. Implementation plan (milestones)
(See `AetherRoadmap.md` and checklist files; these are authoritative for sequencing and gates.)

### 12.1 M0 — Bootstrap substrate
- WSL2 + mirrored networking
- NVIDIA GPU visible in WSL
- Ollama installed
- Initial pin manifest created
- Bootstrap status snapshot captured and tracked

### 12.2 M1 — Core backend + persistence
- Core REST contract (incl. list/search + title update)
- SSE streaming
- SQLite operational policy + migration mechanism
- Boundary enforcement
- Export schema
- Windows↔WSL reachability proof + fallback doc

### 12.3 M2 — General + coding roles (core assistant)
- Role/tier mapping from `pinned.yaml`
- Per-conversation pinning
- Offline validation

### 12.4 M3 — Windows launcher + autostart + backups
- Lifecycle + status --json
- Standardized errors
- Backup Bundle backup/restore

### 12.5 M4 — Desktop-native UI (MVP)
- WPF UI layered over Core API
- Streaming chat (SSE)
- conversation list/search + title edit
- export + autostart toggle

### 12.6 M5 — Tailnet access (post-MVP)
- Windows Tailscale Serve publishes API to tailnet
- Remote devices use CLI-only against API

### 12.7 M6 — Agent mode (post-MVP)
- Safe tools + allowlists
- Strict plan→approval→act protocol

---

## 13. Risks and mitigations (WSL2-specific)
- R1: Windows↔WSL networking quirks
  - Mitigation: mirrored networking; explicit reachability gate; bridge/proxy fallback.
- R2: SQLite performance on `/mnt/*`
  - Mitigation: DB lives in WSL filesystem.
- R3: GPU not used in WSL
  - Mitigation: verify `nvidia-smi`; capture deterministic GPU evidence; expose via `status --json`.
- R4: Windows `localhost` proxy slowness
  - Mitigation: canonicalize to `127.0.0.1` and explicitly avoid `localhost` for API calls.

---

## 14. Definition of Done (MVP = M4)
MVP is done when:
- Desktop UI supports General+Coding in Fast/Thinking (manual role/tier selection)
- Conversations persist across reboot
- Exports work (Markdown + JSON with schema_version + model tag+digest)
- Autostart toggle works
- Offline operation verified

Post-MVP (separate milestones):
- M5: Tailnet API + remote CLI access
- M6: Agent tools

---

## 15. Quirks and operational gotchas (must-follow)
### 15.1 Canonical URL (Windows)
- **Always prefer** `http://127.0.0.1:8484` (Core) and `http://127.0.0.1:11434` (Ollama).
- Avoid `localhost` unless you explicitly force no proxy behavior and have measured it.

### 15.2 PowerShell vs Bash context
- `systemctl` and `sudo` are **Linux/WSL** commands; they only work under:
  - `wsl.exe -- bash -lc '<command>'`
- Do not paste C# code into PowerShell; edit `Program.cs` (or other source files) and build.

### 15.3 WSL command passing pitfalls
When passing multi-line scripts into `wsl.exe -- bash -lc $cmd`:
- Strip CRLF before execution (PowerShell heredocs often include `\r`).
- Prefer single-quoted heredoc delimiters on the Bash side (`<<'SQL'`) to prevent interpolation.
- When deleting `*-wal` / `*-shm`, always use `rm -f -- ...` (the `--` prevents dash-prefixed mis-parsing).

### 15.4 SQLite WAL verification nuance
- WAL/SHM sidecars may not appear until there is write activity; verification should use SQLite pragmas and a simple write in the same connection.

