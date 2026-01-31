# M6 — Agent Mode (Safe Tools) Checklist

## Objective
Add automation with strict safety, explicit approvals, and auditability.

## Tasks
- [ ] Pull `gpt-oss:20b`
- [ ] Optional `gpt-oss-safeguard:20b`
- [ ] Implement tool dispatcher (Core):
  - calculator (pure)
  - local_search (read-only; allowlisted)
  - time
- [ ] Enforce tool allowlists using the shared filesystem boundary policy
- [ ] Implement strict two-phase plan-then-act protocol:
  - PLAN: emit a structured plan (tools, args, allowlist targets, expected outputs, risks)
  - APPROVAL: require explicit user approval before any tool executes
  - ACT: execute tools, persist results
- [ ] Implement full audit logging for Agent sessions:
  - plan event
  - approval event
  - tool call event(s)
  - tool result event(s)
- [ ] Represent plan/approval/tool events in `messages.meta_json` deterministically
- [ ] Ensure exports (versioned JSON schema) can represent tool events deterministically

## Acceptance
- [ ] Agent performs tool tasks correctly
- [ ] No tool runs without explicit approval
- [ ] All tool calls/results are logged (DB + audit log)
- [ ] Tool allowlists enforced; boundary violations return standardized errors
- [ ] No unauthorized side effects

## Artifacts
- Tool audit logs
- Updated security notes
- Example exported conversation containing plan→approval→tool events
