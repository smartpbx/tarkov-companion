# Tarkov Companion Permanent Agent Rules

Read the rules below before touching anything. **Where to read what** at the end of this file is
the orientation map: what this project is, how it works, and what it is meant to become.

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
   CI, use the maintainer's remote development container, whose address is kept
   outside this repository, after verifying the remote checkout path. Never
   silently fall back to local execution.
6. Do not run multiple heavy jobs concurrently on that container; check its
   existing workload first and clean up agent processes when finished.
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

## Where to read what

Start here, in this order. Everything below is in the repository; nothing important about this
project lives only in somebody's head or only in an issue.

| Question | File |
| --- | --- |
| What is this and what does it do? | `README.md` |
| What may it never do? | `docs/SAFETY.md`, enforced by `scripts/audit-safety.sh` |
| How is it put together? | `docs/ARCHITECTURE.md` |
| Why is it built that way? | `docs/adr/`, one file per decision |
| What is it for, as a product? | `docs/PRODUCT.md` |
| What is left to do? | GitHub issues, then `docs/BACKLOG.md` for what has no issue |
| How do I work on it? | `CONTRIBUTING.md` — branches, worktrees, the verification gate |
| How do I run the tests? | `docs/TESTING.md`, and never on the workstation |

Then by subject, when the task touches one:

| Subject | File |
| --- | --- |
| The map: tiles, layers, projection, markers | `docs/MAPS.md` |
| Screenshots, OCR, what the game actually writes | `docs/RECOGNITION.md`, `docs/research/EFT_SCREENSHOT_FACTS.md` |
| The game's logs, line by line | `docs/research/EFT_LOG_FACTS.md` |
| The database and its migrations | `docs/DATABASE.md` |
| Where the game data comes from | `docs/DATA_SOURCES.md` |
| The group relay's protocol | `docs/GROUP_RELAY.md` |
| Running and deploying the relay | `deploy/group-server/README.md` |
| Where things run, and what to do when they break | `docs/OPERATIONS.md` |
| Licences and third-party obligations | `docs/LICENSING.md` |
| Windows packaging and verification | `docs/WINDOWS.md`, `docs/WINDOWS_VERIFICATION.md` |

Three habits this repository has, which are not obvious from the code:

1. **A remark says why, not what.** The comment on a class is usually the history of a bug that
   class exists to prevent. Read it before changing the thing it guards, and when you fix
   something subtle, leave the same kind of note behind.
2. **A claim is measured, not asserted.** "The catalog publishes interior bounds" became useful
   only once somebody counted them. Numbers in docs and commit messages are expected to be real
   and reproducible.
3. **The sweeps are ratchets.** `scripts/sweep-prose.sh`, `scripts/sweep-unread.sh` and
   `scripts/audit-safety.sh` fail the build rather than warn. If one blocks you, the answer is
   almost never to add an allowlist entry.
