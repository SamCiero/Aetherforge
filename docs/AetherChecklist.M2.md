# M2 — General + Coding Roles (Core Assistant) Checklist

## Objective
Deliver a usable core assistant (CLI-first): two roles × two tiers, offline-capable, acceptance-tested.

## Tasks
- [ ] Pull remaining models:
  - `qwen2.5:14b-instruct`
  - `qwen2.5-coder:7b-instruct`
  - `qwen2.5-coder:14b`
- [ ] Update `D:\Aetherforge\config\pinned.yaml` with all required model tag+digest pairs
- [ ] Implement role/tier → model mapping from `pinned.yaml`
- [ ] Manual routing selection
  - CLI flags (`--role`, `--tier`) in the launcher (or equivalent API selection input)
- [ ] Pin model per conversation at creation time
- [ ] Tune generation parameters per role/tier
- [ ] Offline validation (no internet)
- [ ] Capture acceptance transcripts for each role/tier

## Acceptance
- [ ] General/Fast answers quickly and coherently
- [ ] General/Thinking shows improved structure (multi-section) vs Fast
- [ ] Coding/Fast produces correct code for simple tasks
- [ ] Coding/Thinking fixes non-trivial bugs
- [ ] Core uses pinned model mapping (tag+digest) and stores it per conversation
- [ ] Exports include model tag+digest for conversations created in this milestone
- [ ] All tests pass offline

## Artifacts
- Updated `pinned.yaml`
- Acceptance transcripts
