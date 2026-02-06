# M5 — Tailnet Access (API + Remote CLI) Checklist

## Objective
Private multi-device access to the same conversations via Tailscale tailnet, without public exposure.

## Tasks
- [ ] Configure Tailscale on SAM-DESKTOP
- [ ] Configure Tailscale Serve on Windows as the single tailnet entrypoint
  - Publish a Windows loopback port (127.0.0.1) that bridges/proxies to the WSL Core API if needed
- [ ] Confirm no public exposure (Funnel disabled)
- [ ] Document remote access steps (tailnet URL/port, expected endpoints)
- [ ] Validate from at least one non-desktop device (CLI-only via any HTTP client):
  - `GET /v1/conversations?limit=...`
  - `POST /v1/chat` SSE
  - `POST /v1/export/{id}`
- [ ] Validate that tailnet membership is the auth boundary (no additional API auth)

## Acceptance
- [ ] Core API reachable via tailnet only
- [ ] Local loopback access unchanged (`http://127.0.0.1:8484` still works)
- [ ] No CGNAT issues (no port forwards required)
- [ ] Same conversations accessible from desktop UI and a remote device (read/list + chat)

## Artifacts
- Tailscale Serve config notes (Windows)
- Remote device validation evidence (logs/transcripts)
