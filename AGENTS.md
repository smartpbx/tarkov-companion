# Tarkov Companion Permanent Agent Rules

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
- Run `scripts/build.sh` and `scripts/test.sh` before committing substantive changes.
- Keep generated output (`bin`, `obj`, local databases, debug captures, packages) out of Git.

## Agent ownership

- Work only in the directories named in the assigned task.
- Coordinate contract changes through the integration owner.
- Do not edit another agent's owned files or cherry-pick/merge into `main`.
- Commit completed work with a focused message and include test evidence in the handoff.
