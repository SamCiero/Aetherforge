# M4 — Desktop-Native UI Checklist (MVP)

## Objective
Provide a Windows-native daily-driver UI layered over the Core API.

## Tasks
- [ ] WPF app skeleton
- [ ] Connect to Core API using canonical loopback (avoid `localhost`):
  - `http://127.0.0.1:8484`
- [ ] Conversation list + search via `GET /v1/conversations?limit=&offset=&q=`
- [ ] Conversation title editing via `PATCH /v1/conversations/{id}`
- [ ] Streaming chat view consuming SSE from `POST /v1/chat`
- [ ] Role/tier selector (manual routing)
- [ ] Export button
- [ ] Autostart toggle UI (wired to launcher)
- [ ] Enforce "no UI-only state" (all persisted via Core)

## Acceptance
- [ ] Feature parity with CLI for:
  - create/select conversation
  - list/search conversations
  - chat (streaming)
  - export
  - change role/tier (new conversation)
  - autostart toggle
- [ ] Stable long-running sessions (hours), no memory growth issues observed
- [ ] No direct DB access from UI (Core API only)
- [ ] No UI-only persisted state

## Artifacts
- UI binaries
- UI design notes
