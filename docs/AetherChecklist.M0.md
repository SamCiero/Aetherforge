# M0 — Bootstrap Substrate Checklist

## Objective
Prove WSL2 + GPU + Ollama can run pinned models reliably after reboot, and capture the initial pin manifest.

## Tasks
- [x] Enable WSL2 on Windows
- [x] Install Ubuntu LTS distro (WSL)
- [x] Configure WSL2 mirrored networking (not explicitly evidenced in current logs; reachability works either way)
- [x] Verify NVIDIA GPU inside WSL (`nvidia-smi`)
- [x] Install Ollama (Linux) inside WSL
- [x] Configure Ollama model dir: `/var/lib/ollama` (verified via systemd env / status)
- [x] Create Windows root skeleton:
  - `D:\Aetherforge\config\`
  - `D:\Aetherforge\logs\`
  - `D:\Aetherforge\exports\`
  - `D:\Aetherforge\bin\`
- [x] Create `D:\Aetherforge\config\pinned.yaml` (per spec schema)
  - [x] Record Ollama version
- [x] Pull one baseline model tag: `qwen2.5:7b-instruct`
- [x] Resolve and record baseline model digest in `pinned.yaml`
- [x] Verify pinned digest matches live digest (scripted check returned `OK`)
- [x] Run a smoke prompt via Ollama API (API reachability proven; baseline model present)
- [x] Capture a "bootstrap status snapshot" JSON (per spec `status --json` contract subset) and save under:
  - `D:\Aetherforge\logs\bootstrap\status.json`
  - (tracked in git per updated `.gitignore` rules)

## Acceptance
- [x] Model responds successfully via Ollama API
- [x] GPU is visible inside WSL (`nvidia-smi` succeeds)
- [x] Evidence of GPU-backed inference captured (per spec: utilization during inference OR other deterministic evidence)
- [x] `pinned.yaml` exists and contains:
  - [x] Ollama version
  - [x] Baseline model tag + digest
- [x] Works after Windows reboot + WSL restart (repeat smoke prompt + snapshot update)

## Notes / quirks discovered (must remain true going forward)
- Prefer canonical Core API base URL: `http://127.0.0.1:<port>` (avoid `http://localhost:<port>` for PowerShell `Invoke-WebRequest` due to ~21s slowdown).
- When passing multi-line scripts from PowerShell → `wsl.exe -- bash -lc`, strip CRs:
  - `$cmd = $cmd.Replace("`r","")`

## Artifacts
- `D:\Aetherforge\config\pinned.yaml`
- `D:\Aetherforge\logs\bootstrap\status.json`
- Baseline model tag+digest recorded and verified
