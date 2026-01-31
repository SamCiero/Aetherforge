# M1 — Core Backend + Persistence Checklist

## Objective
Establish the backend contract and durable storage, with Windows↔WSL reachability proven and a deterministic, canonical base URL that avoids `localhost` slowness.

## Tasks

### 1) Core service skeleton (WSL)
- [x] Establish Core service project location in-repo (current structure)
  - Windows: `D:\Aetherforge\src\Aetherforge.Core`
  - WSL path: `/mnt/d/Aetherforge/src/Aetherforge.Core`
  - NOTE: Prior `/opt/aetherforge` location is deferred until we intentionally add install/service packaging (system-first layout); do not move code yet.
- [x] Create `Aetherforge.Core` ASP.NET Core service project and add to solution
  - [x] `dotnet new web -n Aetherforge.Core -o src\Aetherforge.Core -f net10.0`
  - [x] Added to `Aetherforge.sln`
- [x] Bind Core to WSL-loopback `http://127.0.0.1:8484` (reachability gate)
- [x] Implement minimal `/v1/status` endpoint (bootstrap-grade subset of final `status --json` contract)

### 2) Windows↔WSL reachability gate (M1 requirement)
- [x] Prove Core API callable from Windows host
  - [x] `curl.exe http://127.0.0.1:8484/v1/status` fast (~<1s observed)
- [x] Identify/record networking quirk: `Invoke-RestMethod http://localhost:8484/...` is ~21s
- [x] Establish canonical base URL for project-wide use: **`http://127.0.0.1:8484`**
  - [x] Treat `localhost` as forbidden/diagnostic-only (avoid UX impact)

### 3) SQLite DB initialization + operational policy
- [x] Install `sqlite3` in WSL for diagnostics/bootstrap (verified: `sqlite3 --version`)
- [x] Create DB directory in WSL filesystem: `/var/lib/aetherforge/`
- [x] Create SQLite DB file: `/var/lib/aetherforge/conversations.sqlite`

- [x] Validate operational PRAGMAs (note: some are per-connection, some persist in DB)
  - [x] `journal_mode=WAL` verified as persisted (`PRAGMA journal_mode;` returns `wal`)
  - [x] `busy_timeout=5000` verified in the *setting connection*
    - Proof (single session): `PRAGMA busy_timeout=5000; PRAGMA busy_timeout;` returns `5000` (connection-scoped; fresh sessions may show `0`)
- [x] Validate DB bootstrap schema
  - [x] `meta` table exists with `schema_version=1` row (verified previously in-session; keep as-is if you have the receipt saved)

- [x] Add Core dependency for SQLite: `Microsoft.Data.Sqlite` package added to `Aetherforge.Core`
- [x] Implement DB initialization inside Core on startup or on `/v1/status` call (actual product behavior)
  - [x] Basic DB open + apply PRAGMAs + meta table + schema_version check (Core reports `db.healthy=true` at `/v1/status`)
  - [ ] Confirm it produces expected sidecars under real load (WAL/shm behavior)
- [ ] Implement schema versioning/migration mechanism (minimal, deterministic)
- [ ] Implement DB lock/backoff rules beyond PRAGMA (retry policy at call sites)

### 4) `pinned.yaml` verification (pins_match / model_digests_match)
- [x] External verification that pinned digest == live digest for baseline model (`qwen2.5:7b-instruct`)
- [x] Add pins verification into `/v1/status` (shows `pins_match`/`model_digests_match` fields)
  - [ ] Ensure it is deterministic and always non-null when both sources are available (currently seen as `null` at times)

### 5) Filesystem boundary enforcement (spec requirement)
- [ ] Implement allowlist boundary policy loader from `D:\Aetherforge\config\settings.yaml`
- [ ] Canonicalize paths before read/write
- [ ] Block reparse points/symlinks/junctions by default
- [ ] Enforce WSL↔Windows bridge rules explicitly (deny unknown mappings)
- [ ] Add explicit negative tests (path traversal + non-allowlisted targets)

### 6) Core REST contract (spec requirement)
- [ ] Implement endpoints:
  - [ ] `POST /v1/conversations` (create)
  - [ ] `GET /v1/conversations/{id}` (read)
  - [ ] `GET /v1/conversations?limit=&offset=&q=` (list + search)
  - [ ] `PATCH /v1/conversations/{id}` (metadata update: title)
  - [ ] `POST /v1/chat` (SSE streaming)
  - [ ] `POST /v1/export/{id}` (export Markdown + JSON)
- [ ] Define and implement SSE event schema for `POST /v1/chat`
- [ ] Implement versioned export schema + deterministic conventions (JSON includes model tag+digest)
- [ ] Implement structured error responses (code/message/detail/hint) for Core API (stable codes)
- [ ] Structured logging (core)
- [ ] Graceful shutdown/startup

### 7) Environment/tooling prerequisites (WSL)
- [x] Install .NET SDK in WSL
  - [x] Resolved `global.json` mismatch by pinning to installed `10.0.100` with `rollForward=latestPatch` (verified via `Get-Content D:\Aetherforge\global.json`)
- [x] Confirm Core can `dotnet run` inside WSL
- [x] Resolve prior quoting/CRLF issues when passing multi-line scripts into `wsl.exe -- bash -lc`
  - [x] Remove CRs (`$cmd = $cmd.Replace("`r","")`) when needed
  - [x] Prefer heredocs (`<<'SQL'`, `<<'PY'`) over nested quote escaping

## Acceptance (UPDATED status)

### Persistence + durability
- [ ] Conversations persist across restarts (requires conversations schema + endpoints)
- [x] DB file exists at `/var/lib/aetherforge/conversations.sqlite` and is writable
- [x] SQLite operational settings are verifiably applied where applicable:
  - [x] WAL persisted (journal_mode=wal)
  - [ ] busy_timeout confirmed in Core connection (connection-scoped; sqlite3 fresh session shows 0 by design)
- [ ] WAL/SHM behavior understood and enforced for backups (checkpoint-on-stop or include sidecars)

### Networking + streaming
- [x] Core is callable from Windows host via `http://127.0.0.1:8484`
- [ ] `POST /v1/chat` streams via SSE end-to-end, including from the Windows host

### API contract features
- [ ] `GET /v1/conversations` supports paging; search (`q=`) works per spec definition
- [ ] `PATCH /v1/conversations/{id}` updates title and is reflected in list/read/export
- [ ] Export produces Markdown + JSON with required fields (`schema_version`, model tag+digest)

### Boundary enforcement
- [ ] No writes outside allowlisted paths (explicit negative tests: traversal + non-allowlisted targets)

### Errors
- [ ] Core errors use the standardized error model with stable codes

## Artifacts (what exists today)
- [x] `src/Aetherforge.Core/` project in repo
- [x] Minimal `GET /v1/status` implemented and reachable
- [x] `global.json` aligned to WSL SDK (`10.0.100`, `rollForward=latestPatch`)
- [x] WSL DB directory + DB created: `/var/lib/aetherforge/conversations.sqlite`
- [x] `Microsoft.Data.Sqlite` package added to Core
- [x] Canonical base URL decision: `http://127.0.0.1:8484` (avoid `localhost`)

## Known quirks to keep front-of-mind (M1 blockers if ignored)
- **Do not use `localhost`** for Windows clients (PowerShell `irm` specifically); use `127.0.0.1`.
- When passing multi-line scripts through `wsl.exe -- bash -lc`, strip CRs and avoid complex quoting.
- Port collisions: if Core is already running in another terminal, `dotnet run` will fail with “address already in use”.
