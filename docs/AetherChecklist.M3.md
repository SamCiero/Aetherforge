# M3 — Windows Launcher + Autostart + Backups Checklist

## Objective
Make Aetherforge operable like a product on Windows: lifecycle control, deterministic status/errors, and reliable backups.

## Tasks
- [ ] Implement `D:\Aetherforge\bin\aetherforge.ps1` commands:
  - `start` (WSL + Ollama + Core)
  - `stop`
  - `status` (human + `--json`)
  - `export <id>`
  - `backup` (create Backup Bundle)
  - `restore --from <bundle>` (restore Backup Bundle)
- [ ] Implement Windows Scheduled Task (On logon)
- [ ] Autostart toggle (`--autostart on|off`) wired to the Scheduled Task
- [ ] Implement Backup Bundle creation:
  - Stop services (cold)
  - Copy Windows tree
  - Copy WSL SQLite DB snapshot into bundle
  - Zip with deterministic bundle naming
- [ ] Implement deterministic restore procedure (scripted), including validation steps
- [ ] Implement standardized error reporting (stable error codes + hints)
- [ ] Ensure tailnet entrypoint port ownership is Windows (Serve runs on Windows; post-MVP)

## Acceptance
- [ ] Reboot starts services automatically when autostart enabled
  - Verified via `aetherforge.ps1 status --json`
- [ ] Autostart toggle works correctly
- [ ] `status --json` conforms to the spec contract (stable keys)
- [ ] Errors are consistent and actionable (code/message/hint)
- [ ] Backup restores cleanly:
  - Restore bundle
  - Services start
  - Conversations present
  - Pins match
  - Export works

## Artifacts
- PowerShell scripts
- Task Scheduler entry
- Backup Bundle sample + restore validation evidence
- Launcher logs
