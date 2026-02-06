# M6 — Agent Mode (Primary + Verifier, Safe Tools) Checklist

## Objective
Add automation with strict safety, explicit approvals, and auditability.

In Agent mode, Core uses **two pinned models**:
- `agent.primary` performs the task
- `agent.verifier` checks the primary output for correctness/safety

Agent remains **post‑MVP** and must not expand blast radius.

---

## Tasks

### 1) Pin the Agent model pair
- [ ] Pull both models required for Agent mode:
  - `gpt-oss:20b` (primary)
  - `gpt-oss-safeguard:20b` (verifier)
- [ ] Update `D:\Aetherforge\config\pinned.yaml` (schema v1):
  - [ ] `models.agent.primary.tag` + `digest` (non-null)
  - [ ] `models.agent.verifier.tag` + `digest` (non-null)
  - [ ] Mark both as `required: true` (or otherwise enforce equivalently)
- [ ] Verify digests match live `/api/tags` and `/v1/status` reports:
  - [ ] `pins_match=true`
  - [ ] `model_digests_match=true` (when tags available)

### 2) Enable Agent mode safely
- [ ] Gate Agent mode behind settings:
  - [ ] `agent.enabled=true` required to use Agent role
  - [ ] `agent.require_plan_approval=true` default
  - [ ] `agent.allow_tools=[...]` default empty unless explicitly set
- [ ] If `agent.enabled=false`, attempting to use role `agent` must return a structured error.

### 3) Agent pipeline: primary → verifier
- [ ] Implement the Agent execution flow:
  - [ ] Run `agent.primary` to produce a candidate result
  - [ ] Run `agent.verifier` to verify the candidate result
  - [ ] Persist both outputs (and verification result) deterministically
- [ ] Ensure stored conversation model fields remain truthful:
  - Conversation records the pinned pair availability in metadata or per-message `meta_json`
  - Exports can reconstruct which model produced which step

### 4) Tool dispatcher (safe tools only)
- [ ] Implement tool dispatcher (Core):
  - `calculator` (pure evaluation)
  - `local_search` (read-only; allowlisted)
  - `time` (read-only)
- [ ] Enforce tool allowlists using the shared filesystem boundary policy:
  - No reads outside `boundary.allow_read_under_wsl`
  - No writes except where explicitly allowed (Agent tools should not require writes by default)
- [ ] No arbitrary shell commands.

### 5) Strict plan → approval → act protocol
- [ ] PLAN: emit a structured plan (tools, args, allowlist targets, expected outputs, risks)
- [ ] APPROVAL: require explicit user approval before any tool executes
- [ ] ACT: execute only approved tool calls; persist results
- [ ] Enforce “no tool runs without approval” at the Core boundary (not just UI).

### 6) Full audit logging + export fidelity
- [ ] Persist an auditable event trail for Agent sessions:
  - plan event
  - approval event
  - tool call event(s)
  - tool result event(s)
  - verifier event (verification outcome)
- [ ] Represent plan/approval/tool/verifier events in `messages.meta_json` deterministically
- [ ] Ensure versioned exports (JSON schema v1) can represent these events deterministically
- [ ] Ensure SSE `error` events surface structured errors mid-stream

---

## Acceptance
- [ ] Agent performs a tool-augmented task correctly using the plan→approval→act protocol
- [ ] Primary step runs and verifier step runs; verification result is visible and persisted
- [ ] No tool runs without explicit approval (hard enforcement)
- [ ] All tool calls/results are logged (DB + audit log)
- [ ] Tool allowlists enforced; boundary violations return standardized errors
- [ ] No unauthorized side effects

---

## Artifacts
- Tool audit logs
- Updated security notes
- Example exported conversation containing:
  - plan → approval → tool calls/results
  - primary output + verifier output
