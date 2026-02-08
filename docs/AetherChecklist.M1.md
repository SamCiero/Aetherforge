# M1 — Core Backend + Persistence

## Objective
Establish a **fully usable Core backend contract** with durable persistence, deterministic behavior, and Windows↔WSL reachability proven.
M1 must leave the system in a state where **M2 (assistant roles), M3 (launcher), and M4 (UI)** are integrations — not guesswork.

---

## Scope Clarification
This milestone **does not** aim to provide a polished assistant UX.
It **does** aim to provide:
- A complete, spec-compliant Core API
- Durable conversation persistence
- Streaming chat via SSE (Windows host validated)
- Deterministic exports
- Enforced filesystem boundaries (allowlists + reparse blocking)
- Stable error semantics
- Settings + pins loading/validation that matches the spec

> **Role/tier glossary (per updated spec)**
> - Roles: `general`, `coding`, `agent`
> - General + Coding tiers: `fast`, `thinking`
> - Agent tiers: `primary`, `verifier` (Agent is a single UX mode; Core uses primary then verifier)

---

## Tasks

### 1) Core service foundation (WSL)
- [x] Core project exists in-repo
  - Windows: `D:\Aetherforge\src\Aetherforge.Core`
  - WSL: `/mnt/d/Aetherforge/src/Aetherforge.Core`
- [x] ASP.NET Core service builds and runs under WSL
- [x] Core binds to canonical loopback:
  - **`http://127.0.0.1:8484`**
- [x] Minimal `/v1/status` endpoint reachable from Windows host
- [ ] Replace bootstrap-only status logic with the **spec status contract** (deterministic fields):
  - [ ] `schema_version` (always 1)
  - [ ] `captured_utc` (UTC ISO-8601)
  - [ ] `core: { reachable, version, base_url }`
  - [ ] `ollama: { reachable, version, base_url, models_dir? }` (models_dir may be null if unknown)
  - [ ] `pins: { pinned_yaml_path, pins_match, model_digests_match, detail }`
  - [ ] `db: { path, healthy, error }`
  - [ ] `gpu: { visible, evidence }`
  - [ ] `tailnet: { serve_enabled, published_port }` (may be false/null in M1)
  - [ ] `files: { settings_exists, pinned_exists }`

---

### 2) Windows↔WSL reachability gate (M1 hard requirement)
- [x] Core reachable from Windows host via:
  - `curl.exe http://127.0.0.1:8484/v1/status`
- [x] Canonical base URL documented and enforced:
  - `http://127.0.0.1:8484`
- [x] `localhost` explicitly treated as forbidden for normal operation
- [ ] SSE streaming validated end-to-end from Windows host (not buffered):
  - [ ] `POST /v1/chat` streams `meta` then `delta` events incrementally

---

### 3) Settings loading + validation (spec requirement)
- [ ] Load `config/settings.yaml` with a real YAML parser
- [ ] Enforce `settings.yaml` schema version = 1
- [ ] Validate required sections and constraints:
  - [ ] `ports.core_bind_url` and `ports.ollama_base_url` are absolute HTTP URLs using host `127.0.0.1`
  - [ ] `defaults.role` in `{general,coding}` (Agent cannot be the default)
  - [ ] `defaults.tier` in `{fast,thinking}`
  - [ ] `pins.mode` in `{strict,fallback}`
  - [ ] `pins.fallback_role` in `{general,coding}` (Agent must not be used as a fallback role)
  - [ ] `pins.fallback_tier` in `{fast,thinking}` (fallback applies only to General/Coding roles)
  - [ ] Boundary config validated (see §5)
- [ ] Ensure settings-load errors are surfaced deterministically:
  - [ ] `/v1/status` `files.settings_exists=true` but includes a clear `detail` somewhere appropriate (or stable error code) when settings invalid
  - [ ] Core startup behavior defined: either fail-fast OR start with safe defaults (but must be explicit and observable)

---

### 4) SQLite persistence + schema (durable)
- [x] DB directory exists in WSL filesystem:
  - `/var/lib/aetherforge/`
- [x] SQLite DB file exists:
  - `/var/lib/aetherforge/conversations.sqlite`
- [x] WAL mode enabled and persisted
- [x] `meta` table exists with `schema_version`
- [ ] Implement full schema (spec-aligned):
  - [ ] `conversations` table:
    - `id`, `created_utc`, `role`, `tier`, `model_tag`, `model_digest`, `title`
    - Note: `tier` is role-dependent (`fast/thinking` for general/coding; `primary/verifier` for agent)
  - [ ] `messages` table:
    - `id`, `conversation_id`, `created_utc`, `sender`, `content`, `meta_json`
- [ ] Add required indices:
  - [ ] `messages(conversation_id)`
  - [ ] `messages(created_utc)` (or `(conversation_id, id)` depending on retrieval strategy)
  - [ ] `conversations(created_utc)` (optional but recommended)
- [ ] Implement minimal deterministic migration mechanism:
  - [ ] schema version check
  - [ ] apply migrations in order
  - [ ] record applied version
- [ ] Enforce foreign keys
- [ ] Implement retry/backoff rules for busy DB (beyond PRAGMA), consistent with spec
- [ ] Ensure DB open/health check used by `/v1/status`

---

### 5) Contracts + DTOs (single source of truth)
- [ ] Define API DTOs in `Aetherforge.Contracts` (or equivalent shared assembly) and use them exclusively:
  - [ ] Status DTOs:
    - `StatusResponse` (+ nested `Core/Ollama/Pins/Db/Gpu/Tailnet/Files` DTOs)
  - [ ] Conversations:
    - `ConversationCreateRequest`
    - `ConversationDto`
    - `ConversationWithMessagesDto`
    - `ConversationListResponse` (includes limit/offset/q echo)
    - `ConversationPatchRequest` (title only)
  - [ ] Messages:
    - `MessageDto`
  - [ ] Chat:
    - `ChatRequest`
    - SSE event payload DTOs:
      - `SseMetaEvent` includes: `conversation_id`, `message_id`, `model_tag`, `model_digest`, `resolution` (nullable)
      - `SseDeltaEvent` includes: `message_id`, `delta_text`
      - `SseDoneEvent` includes: `message_id`
  - [ ] Export:
    - `ConversationExportV1` + nested `ExportConversation`, `ExportModel`, `ExportMessage`
    - `ExportResponse` (paths)
- [ ] Define canonical `ErrorResponse` DTO:
  - `code`, `message`, optional `detail`, optional `hint`
- [ ] Core uses Contracts DTOs exclusively (no anonymous/inline response shapes)

---

### 6) Filesystem boundary enforcement (spec requirement)
- [ ] Load boundary configuration from `config/settings.yaml`
- [ ] Enforce boundary schema and validation:
  - [ ] `boundary.bridge_rules[]` exists and has at least one rule
  - [ ] `boundary.roots.wsl.{config,exports,logs}` exist and are absolute WSL paths
  - [ ] `boundary.allow_write_under_wsl` exists and contains at least one root
  - [ ] `boundary.allow_read_under_wsl` is allowed to be empty
  - [ ] `boundary.block_reparse_points` honored (default true)
- [ ] Canonicalize all paths before access
- [ ] Enforce “descendant of allowlisted root” rule for writes
- [ ] Block symlinks/reparse points by default (best-effort detection is acceptable but must be deterministic in behavior)
- [ ] Explicitly deny unknown/unsafe WSL↔Windows mappings per `bridge_rules`
- [ ] Add negative tests/evidence:
  - traversal attempts (e.g. `..`)
  - non-allowlisted write targets
  - reparse/symlink paths (when enabled)
- [ ] **Environment override note (spec-aligned):**
  - If `AETHERFORGE_EXPORTS_ROOT` is supported, ensure boundary allowlist must include it (either require operator update or auto-add deterministically, but pick one and document/implement)

---

### 7) Pins manifest loading + verification (deterministic)
- [ ] Parse `config/pinned.yaml` using a real YAML parser
- [ ] Enforce pinned schema version = 1
- [ ] Support null digests (unpinned placeholders)
- [ ] Support optional per-entry `required: bool`
- [ ] Normalize digests:
  - lowercase
  - strip `sha256:` prefix if present
  - validate 64-hex when non-null
- [ ] Verify digests against Ollama `/api/tags` when tags available
- [ ] `/v1/status` pins fields follow spec semantics:
  - [ ] `pins_match`:
    - `null` only if manifest missing/unreadable
    - `false` if required digests missing/invalid
    - `true` if required digests present and structurally valid
  - [ ] `model_digests_match`:
    - `null` if tags cannot be fetched or manifest missing
    - otherwise bool reflecting digest equality vs live tags
  - [ ] `detail` includes human-readable reason when pins_match is false (or manifest unreadable)

---

### 8) Core REST API (Phase 1 contract)
> M1 delivers the **contract and mechanics**. M2 is where role/tier coverage becomes a “daily assistant baseline” with acceptance-tested model suites.

#### Status
- [ ] `GET /v1/status` implements full spec schema (see §1)

#### Conversation lifecycle
- [ ] `POST /v1/conversations`
  - [ ] Validates role/tier:
    - roles: `general|coding|agent`
    - tiers: `fast|thinking` (general/coding) and `primary|verifier` (agent)
  - [ ] Requires pinned manifest present (or deterministic error)
  - [ ] Applies pins policy (`settings.pins.mode`)
    - strict: missing/unpinned => error
    - fallback: missing/unpinned => resolve to `pins.fallback_role/tier` entry
  - [ ] Pins `{model_tag, model_digest}` at creation time
  - [ ] Preserves requested `{role, tier}` even when fallback is used (truthful model fields still reflect actual model used)
- [ ] `GET /v1/conversations/{id}`
- [ ] `GET /v1/conversations?limit=&offset=&q=`
  - [ ] Paging required
  - [ ] Search by title (minimum)
- [ ] `PATCH /v1/conversations/{id}`
  - [ ] Title update only

#### Chat (streaming)
- [ ] `POST /v1/chat`
  - [ ] Server-Sent Events (SSE)
  - [ ] Required events:
    - `meta` (must include `resolution` when fallback applies)
    - `delta`
    - `done`
    - `error`
  - [ ] `meta.resolution` format: `fallback:<role>.<tier>` (nullable when no fallback)
  - [ ] Streaming works end-to-end from Windows host (incremental deltas)
  - [ ] User + assistant messages persisted to DB
  - [ ] Cancellation does not deadlock DB or Ollama; partial assistant buffer persists safely

#### Export
- [ ] `POST /v1/export/{id}`
- [ ] Writes to allowlisted exports root only (boundary enforced)
- [ ] Generates:
  - Markdown transcript
  - JSON export
- [ ] JSON export includes:
  - `schema_version`
  - `generated_utc`
  - `core_version`
  - conversation metadata
  - model tag + digest
  - messages with `meta_json`

---

### 9) Error model (mandatory)
- [ ] All API errors use structured `ErrorResponse`:
  - `code`, `message`, optional `detail`, optional `hint`
- [ ] Stable error codes defined and reused (no one-off strings sprinkled everywhere)
- [ ] SSE emits `event: error` with structured payload (same DTO)
- [ ] No raw stack traces leak to clients
- [ ] Boundary violations return deterministic boundary error codes and include allowed roots in `hint` when safe

---

## Acceptance Criteria

### Persistence & durability
- [ ] Conversations persist across Core restarts
- [ ] Messages persist and replay correctly
- [ ] WAL behavior understood and compatible with future backups (checkpoint/copy rules documented)

### Networking & streaming
- [ ] Core reachable from Windows host via canonical URL
- [ ] SSE chat streams incrementally (not buffered)
- [ ] Cancellation behaves cleanly

### API contract
- [ ] All endpoints exist and conform to spec shapes (DTOs)
- [ ] Paging and search work
- [ ] Title edits reflected in list/read/export

### Settings + pins determinism
- [ ] Invalid `settings.yaml` or `pinned.yaml` produces stable, actionable errors and is visible in `/v1/status`
- [ ] `pins_match` and `model_digests_match` obey the “null only when impossible” rule

### Boundary enforcement
- [ ] Writes outside allowlisted paths are blocked
- [ ] Violations return structured boundary errors

### Errors
- [ ] All failure modes return stable, actionable errors

---

## Artifacts (expected at M1 completion)
- Core API implementing the full Phase-1 contract (including `/v1/status`)
- Settings + pins loaders with deterministic validation behavior
- SQLite DB with conversations + messages schema + indices + migration mechanism
- Deterministic export samples (MD + JSON)
- Boundary enforcement tests/evidence
- SSE streaming evidence from Windows host
- Updated `/v1/status` output matching spec

---

## Known Quirks (must remain enforced)
- **Never use `localhost`** for Windows clients
- Strip CRLF when passing scripts via `wsl.exe`
- Expect WAL sidecars only after write activity
- Port collisions must fail loudly and clearly

---

## M1 Exit Rule
**Do not start M2 until all Acceptance Criteria above are met.**
