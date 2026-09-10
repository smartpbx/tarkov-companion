# Tarkov Companion Permanent Agent Rules

## Workstation safety — non-negotiable

This repository triggered two workstation hard lockups on 2026-09-10. A local
.NET debugger run was associated with kernel page-table corruption (`BUG: Bad
page map`), followed later by a 12-CPU soft lockup and network-stack deadlock.
These rules override every conflicting instruction elsewhere in this repository:

1. Never attach, launch, or use a .NET debugger on Clayton's workstation.
2. Never run .NET workloads locally. This includes `dotnet`, MSBuild,
   `scripts/bootstrap.sh`, `scripts/build.sh`, `scripts/test.sh`,
   `scripts/package-windows.sh`, the simulator, and any IDE build/test/debug
   action.
3. Never start Docker, a container stack, or the local Windows VM for this
   repository on Clayton's workstation.
4. Do not launch local Orca worker/sub-agent terminals for this repository.
   The current assistant may make edits, use Git/GitHub CLI, and run short
   read-only inspection commands only.
5. Build- and test-shaped work must run in GitHub Actions. If it cannot run in
   CI, use the Proxmox development container CT 114 via
   `ssh proxmox 'pct exec 114 -- bash -lc "cd /root/repos/<repo> && ..."'`
   after verifying the remote checkout path. Never silently fall back to local
   execution.
6. Do not run multiple heavy jobs concurrently on CT 114; check its existing
   workload first and clean up agent processes when finished.
7. If work requires anything beyond editing files, Git, `gh`, or short
   read-only commands on the workstation, stop and ask Clayton first.
8. Every delegated task or handoff must repeat these workstation restrictions.

1. This application is external and read-only relative to Escape from Tarkov.
2. Never read or write Escape from Tarkov process memory.
3. Never inject code or DLLs, and never hook the game renderer.
4. Never inspect, intercept, or decode Escape from Tarkov network traffic.
5. Never generate gameplay mouse or keyboard input.
6. Never automate flea purchases, sales, inventory actions, aiming, or combat.
7. Never implement enemy detection, ESP, radar, or live player tracking.
8. Do not render an in-game overlay in v1.
9. Core and domain projects must remain platform-independent.
10. Windows integrations must be behind interfaces and fixture-testable.
11. Use `json.tarkov.dev` as the primary runtime structured game-data source unless current official guidance changes and the change is documented.
12. Do not copy RatScanner, TarkovMonitor, or other reference source without an explicit license review and ADR. Default to clean-room implementation.
13. Do not scrape eft-ammo or other websites when structured public data exists.
14. Preserve third-party attribution and license obligations.
15. Never commit secrets or user tokens.
16. Add tests for substantive logic.
17. Record major architectural changes in `docs/adr/`.
18. Never present predictions as live detections.
19. Preserve confidence, source, and timestamp for uncertain intelligence.
20. Do not run destructive commands outside this repository or an assigned worktree.
21. Read this file before editing.

## Build and code conventions

- Target .NET 10 and C# 14 with nullable reference types enabled.
- Keep `TarkovCompanion.Core` free of Avalonia, Windows, SQLite, HTTP, OCR-native, capture, and filesystem-watcher dependencies.
- Put orchestration in Application, external I/O in Infrastructure, Windows P/Invoke in Platform.Windows, and presentation in App.
- Prefer small domain-specific interfaces over generic repositories.
- Use UTC timestamps for persistence and reports.
- Propagate cancellation for I/O and bounded background work.
- Treat external JSON as untrusted: tolerate unknown fields and fail clearly for missing required fields.
- Do not persist captured screen images unless Debug Capture is explicitly enabled.
- Require `scripts/build.sh` and `scripts/test.sh` (or equivalent targeted
  checks) in GitHub Actions before integrating substantive changes. Never run
  them on Clayton's workstation.
- Keep generated output (`bin`, `obj`, local databases, debug captures, packages) out of Git.

## Agent ownership

- Work only in the directories named in the assigned task.
- Coordinate contract changes through the integration owner.
- Do not edit another agent's owned files or cherry-pick/merge into `main`.
- Commit completed work with a focused message and include test evidence in the handoff.
