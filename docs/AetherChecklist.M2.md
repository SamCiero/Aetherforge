# M2 — General + Coding Roles (Core Assistant) Checklist (Spec/Roadmap-Aligned)

## Objective
Deliver a usable **CLI-first** core assistant for daily evaluation:
- General + Coding roles with `fast` and `thinking` tiers
- Deterministic per-conversation pinning (model tag + digest)
- Spec-compliant **pins policy** (`strict` / `fallback`) and truthful reporting
- Offline-capable operation (no internet required once models are pulled)
- Acceptance tested via an API/CLI harness (launcher/UI come later)

> Scope note:
> M2 does **not** require a full Windows launcher (that’s M3).  
> M2 can use `curl.exe`, PowerShell scripts, or a small harness to hit the Core API.

---

## Tasks

### 1) Pull and pin the General/Coding model suite
- [ ] Pull remaining models in Ollama (WSL):
  - `qwen2.5:14b-instruct`
  - `qwen2.5-coder:7b-instruct`
  - `qwen2.5-coder:14b`
- [ ] Update `D:\Aetherforge\config\pinned.yaml` (schema v1) with **tag + digest** for all General/Coding tiers:
  - `models.general.fast`
  - `models.general.thinking`
  - `models.coding.fast`
  - `models.coding.thinking`
- [ ] Mark `required: true` for all General/Coding entries (recommended once pinned)
- [ ] Agent pins gate per spec:
  - When `agent.enabled=false`, `pinned.yaml` does **not** need `models.agent.*` entries (omit them).
  - Core rejects `role=agent` requests until Agent mode is implemented (M6).
- [ ] Normalize digests in `pinned.yaml`:
  - lowercase 64-hex
  - no `sha256:` prefix stored
- [ ] Update `captured_utc`
- [ ] Write a dated backup copy of the previous `pinned.yaml` alongside it (upgrade/rollback rule)

### 2) Pins policy behavior (strict vs fallback)
- [ ] Ensure Core implements the pins policy (per spec):
  - `strict`: requested role/tier missing or digest null ⇒ **conversation creation fails**
  - `fallback`: requested role/tier missing or digest null ⇒ resolve to `pins.fallback_role/tier`
- [ ] Ensure fallback is **truthful**:
  - Conversation stores the **actual** resolved `model_tag` + `model_digest`
  - Conversation still preserves the user’s requested `role` + `tier`
- [ ] Ensure SSE `meta` event includes `resolution` only when fallback is used:
  - `resolution: "fallback:<role>.<tier>"` (format is fixed by spec)

> Recommended M2 config:
> - Set `settings.yaml -> pins.mode: strict` once General/Coding are fully pinned,
>   so M2 validates the “no unpinned use” rule.

### 3) Manual routing selection (CLI-first)
- [ ] Provide a minimal harness (PowerShell script, bash script, or docs) to:
  - call `POST /v1/conversations` with `{ role, tier }`
  - call `POST /v1/chat` SSE streaming against the created conversation
- [ ] Verify routing works for all four combinations:
  - `general.fast`
  - `general.thinking`
  - `coding.fast`
  - `coding.thinking`

- [ ] When changing role or tier, start a **new conversation** (do not reuse an existing conversation when the user switches).

### 4) Generation parameters per role/tier (settings-driven)
- [ ] Define baseline generation parameters in `settings.yaml` under `generation.by_profile`:
  - `general.fast` vs `general.thinking`
  - `coding.fast` vs `coding.thinking`
- [ ] Implement passing supported parameters through to Ollama chat requests
  (only those you intentionally support; null means default)
- [ ] Confirm parameter changes have measurable behavioral effect (e.g., response length/structure)

### 5) Offline validation (no internet)
- [ ] Validate the system works with **no internet connectivity**:
  - Models are already pulled
  - Core runs and chats succeed using only local endpoints
- [ ] Confirm `/v1/status` reflects a healthy offline state:
  - `ollama.reachable=true`
  - `pins_match=true` (now that required digests are present)
  - `model_digests_match=true` (when tags available)
  - `db.healthy=true`

### 6) Deterministic exports for acceptance evidence
- [ ] For each role/tier acceptance run:
  - Create a conversation
  - Run a short scripted chat scenario
  - Export via `POST /v1/export/{id}`
- [ ] Verify exports contain:
  - model tag + digest
  - full message transcript
  - message `meta_json` preserved (if present)
- [ ] Confirm exports land under:
  - `D:\Aetherforge\exports\YYYY-MM-DD\...`
  - and boundary enforcement permits only allowlisted roots

### 7) Capture acceptance transcripts (repeatable)
- [ ] Capture acceptance transcripts for each role/tier as:
  - the Markdown export file (canonical)
  - plus optionally raw SSE logs (helpful for debugging streaming)

---

## Acceptance

### Behavioral expectations (minimum)
- [ ] **General/Fast** answers quickly and coherently
- [ ] **General/Thinking** shows improved structure vs Fast (e.g., multi-section response)
- [ ] **Coding/Fast** produces correct code for simple tasks
- [ ] **Coding/Thinking** can fix a non-trivial bug (multi-step reasoning in the output, not chain-of-thought)

### Determinism + pinning
- [ ] Core uses pinned mapping and stores `{model_tag, model_digest}` per conversation
- [ ] `pins.mode=strict` rejects unpinned requests (missing/empty digest) with a structured error
- [ ] If `pins.mode=fallback` is enabled for a test case:
  - fallback resolves correctly to `pins.fallback_role/tier`
  - SSE `meta.resolution` reports `fallback:<role>.<tier>`
  - stored model_tag/digest reflect the fallback model (truthful)

### Exports
- [ ] Exports include model tag+digest for conversations created in this milestone
- [ ] JSON export is schema v1 and includes required fields (schema_version, generated_utc, core_version, conversation, model, messages)

### Offline
- [ ] All acceptance tests pass with internet disabled (local only)

---

## Artifacts
- Updated `D:\Aetherforge\config\pinned.yaml` (+ dated backup copy)
- Updated `D:\Aetherforge\config\settings.yaml` generation parameters (and pins.mode if changed)
- 4 acceptance exports (MD + JSON), one per role/tier:
  - `general.fast`, `general.thinking`, `coding.fast`, `coding.thinking`
- Optional: raw SSE logs demonstrating incremental streaming + `meta.resolution` behavior
