# M1 — Core Backend + Persistence (Expanded, Contract-Complete)

## Objective
Establish a **fully usable Core backend contract** with durable persistence, deterministic behavior, and Windows↔WSL reachability proven.  
M1 must leave the system in a state where **M2 (assistant roles), M3 (launcher), and M4 (UI)** are integrations — not guesswork.

---

## Scope Clarification
This milestone **does not** aim to provide a polished assistant UX.  
It **does** aim to provide:
- A complete, spec-compliant Core API
- Durable conversation persistence
- Streaming chat via SSE
- Deterministic exports
- Enforced filesystem boundaries
- Stable error semantics

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
- [ ] Replace bootstrap-only status logic with deterministic status contract fields

---

### 2) Windows↔WSL reachability gate (M1 hard requirement)
- [x] Core reachable from Windows host via:
  - `curl.exe http://127.0.0.1:8484/v1/status`
- [x] Canonical base URL documented and enforced:
  - `http://127.0.0.1:8484`
- [x] `localhost` explicitly treated as forbidden for normal operation
- [ ] SSE streaming validated end-to-end from Windows host

---

### 3) SQLite persistence + schema (durable)
- [x] DB directory exists in WSL filesystem:
  - `/var/lib/aetherforge/`
- [x] SQLite DB file exists:
  - `/var/lib/aetherforge/conversations.sqlite`
- [x] WAL mode enabled and persisted
- [x] `meta` table exists with `schema_version`
- [ ] Implement full schema:
  - [ ] `conversations` table
  - [ ] `messages` table
- [ ] Add required indices (conversation_id, created_utc)
- [ ] Implement minimal deterministic migration mechanism
- [ ] Enforce foreign keys
- [ ] Implement retry/backoff rules for busy DB (beyond PRAGMA)

---

### 4) Contracts + DTOs (single source of truth)
- [ ] Define API DTOs in `Aetherforge.Contracts`:
  - `ConversationCreateRequest`
  - `ConversationDto`
  - `MessageDto`
  - `ConversationWithMessagesDto`
  - `ConversationListResponse`
  - `ChatRequest`
- [ ] Define canonical `ErrorResponse`:
  - `code`
  - `message`
  - `detail` (optional)
  - `hint` (optional)
- [ ] Core uses Contracts DTOs exclusively (no anonymous response shapes)

---

### 5) Filesystem boundary enforcement (spec requirement)
- [ ] Load boundary configuration from `config/settings.yaml`
- [ ] Define allowlisted roots (minimum):
  - config
  - logs
  - exports
- [ ] Canonicalize all paths before access
- [ ] Enforce “descendant of allowlisted root” rule
- [ ] Block symlinks/reparse points by default
- [ ] Explicitly deny unknown WSL↔Windows mappings
- [ ] Add negative tests:
  - traversal attempts
  - non-allowlisted write targets

---

### 6) Core REST API (Phase 1 contract)

#### Conversation lifecycle
- [ ] `POST /v1/conversations`
  - Validates role/tier
  - Pins model tag + digest at creation time
- [ ] `GET /v1/conversations/{id}`
- [ ] `GET /v1/conversations?limit=&offset=&q=`
  - Paging required
  - Search by title (minimum)
- [ ] `PATCH /v1/conversations/{id}`
  - Title update only

#### Chat (streaming)
- [ ] `POST /v1/chat`
  - Server-Sent Events (SSE)
  - Required events:
    - `meta`
    - `delta`
    - `done`
    - `error`
- [ ] Streaming works end-to-end from Windows host
- [ ] Assistant messages persisted to DB
- [ ] Cancellation does not deadlock DB or Ollama

#### Export
- [ ] `POST /v1/export/{id}`
- [ ] Writes to allowlisted exports root only
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

### 7) Error model (mandatory)
- [ ] All API errors use structured error responses
- [ ] Stable error codes defined and reused
- [ ] SSE emits `event: error` with structured payload
- [ ] No raw stack traces leak to clients

---

### 8) Pins verification (deterministic)
- [ ] Parse `config/pinned.yaml` using a real YAML parser
- [ ] Enforce pinned schema version
- [ ] Verify digests against Ollama `/api/tags`
- [ ] `/v1/status` reports:
  - `pins_match` (bool or null)
  - `model_digests_match` (bool or null)
- [ ] Never emit nondeterministic `null` unless verification is impossible

---

## Acceptance Criteria

### Persistence & durability
- [ ] Conversations persist across Core restarts
- [ ] Messages persist and replay correctly
- [ ] WAL behavior understood and compatible with future backups

### Networking & streaming
- [ ] Core reachable from Windows host via canonical URL
- [ ] SSE chat streams incrementally (not buffered)
- [ ] Cancellation behaves cleanly

### API contract
- [ ] All endpoints exist and conform to spec shapes
- [ ] Paging and search work
- [ ] Title edits reflected in list/read/export

### Boundary enforcement
- [ ] Writes outside allowlisted paths are blocked
- [ ] Violations return structured boundary errors

### Errors
- [ ] All failure modes return stable, actionable errors

---

## Artifacts (expected at M1 completion)
- Core API implementing full Phase-1 contract
- SQLite DB with conversations + messages
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
