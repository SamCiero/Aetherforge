# Aetherforge — Project Roadmap

This roadmap is authoritative for sequencing work on Aetherforge.
Each milestone has a dedicated checklist file with explicit completion gates.

Progression rule: do not start a milestone until the previous one is complete.

## Canonical local URL + PowerShell quirk
- Canonical loopback host is `127.0.0.1` for all local calls (Windows→WSL and Windows-local).
- Do **not** use `localhost` (PowerShell/WinHTTP proxy settings can route it and add large latency).
  - Prefer `Invoke-RestMethod http://127.0.0.1:<port>/... -NoProxy` for local calls.
  - For raw timing probes, prefer `curl.exe http://127.0.0.1:<port>/...`.

## Milestones

### M0 — Bootstrap Substrate
Foundation: WSL2, GPU passthrough, Ollama runtime, initial pin manifest.
Goal: prove the platform assumptions.

### M1 — Core Backend + Persistence
Linux-side core service, REST contract (including conversation list/search + title updates), SSE chat streaming, SQLite persistence policy, boundary enforcement, deterministic export schema.
Goal: stable backend contract before any UI.

### M2 — General + Coding Roles (Core Assistant)
Two roles × two tiers, offline-capable, pinned model mapping, acceptance tested via CLI/API harness.
Goal: functional assistant baseline for daily evaluation via terminal.

### M3 — Windows Launcher + Autostart + Backups
Windows control plane (start/stop/status/chat/export), status --json contract, standardized errors, Scheduled Task autostart toggle, Backup Bundle backup/restore.
Goal: product-like operability for local use.

### M4 — Desktop-Native UI (MVP)
Windows-native UI (WPF recommended) layered over Core API; streaming chat (SSE), conversation list/search, exports, autostart toggle.
Goal: daily-driver UX. MVP is achieved at M4.

### M5 — Tailnet Access (API + Remote CLI)
Tailnet exposure via Tailscale Serve on Windows as the single entrypoint; remote devices use CLI-only against the Core API.
Goal: seamless multi-device access to the same conversations (post-MVP).

### M6 — Agent Mode (Safe Tools)
Tool-augmented reasoning with a strict two-phase plan→approval→act protocol, allowlists, and full audit logging.
Goal: controlled automation without expanding blast radius (post-MVP).
