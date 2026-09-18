# Tarkov Companion Permanent Agent Rules

Read the rules below before touching anything. **Where to read what** at the end of this file is
the orientation map: what this project is, how it works, and what it is meant to become.

## Development-host resource safety

This repository triggered two hard lockups on Clayton's workstation on 2026-09-10. A local
.NET debugger run was associated with kernel page-table corruption (`BUG: Bad page map`),
followed later by a 12-CPU soft lockup and network-stack deadlock. Development has since moved
to the dedicated host named `dev`; the restrictions below preserve that boundary without
preventing work on the development host.

1. Never build, test, debug, start Docker, launch a VM, or run repository agents on Clayton's
   workstation. If the current host is not the dedicated development host, stop and ask Clayton.
2. .NET workloads, debuggers, containers, VMs, and Orca sub-agents are permitted on `dev`.
3. Check CPU load, available memory, disk space, and existing repository processes before a
   heavy job or a multi-agent wave.
4. Do not run multiple heavy builds, test suites, containers, or VMs concurrently unless the
   resource check shows comfortable headroom. Prefer one shared verification job after parallel
   editing work instead of one full build per agent.
5. Clean up agent, debugger, VM, and container processes when their work is finished.
6. GitHub Actions remains the required integration evidence for substantive changes; local
   verification on `dev` is supplementary and must not replace the CI gate.
7. Every delegated task or handoff must name its worktree/owned paths, repeat the anti-cheat
   boundaries below, and tell the worker to keep heavy verification serialized.

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

## Cost discipline (every agent: Codex, Claude, workers)

Measured, not guessed. Between 2026-09-14 and 2026-09-16 one Codex coordinator session used up
Clayton's weekly ChatGPT limit. Its own session log shows 42 hours, about 8,300 model calls and
about **1 billion input tokens against 2.6 million output tokens**, with reasoning effort at the
maximum ("ultra") on every call. Where those tokens went: 63% reading git/GitHub state, 18%
polling CI and sub-agents (1,374 status checks), 10% coordinating agents, 8% editing code. In the
same period about 500 CI runs finished with 129 failures and 75 cancellations; at least 18 commits
only fixed compile or nullability errors, each one a 13-minute Windows CI round trip. 28 of 85
worktrees were audit, re-audit or "final audit" passes, while many merged foundations were never
called by the running app and the V2 interface was never given a visual design pass.

The bill is dominated by re-reading context, not by writing code. So:

1. **Keep contexts small.** Start a fresh session per package instead of resuming one that has
   grown past ~100K tokens. Hand bulky reads (logs, diffs, issue sweeps) to a worker and keep only
   its conclusion.
2. **Reasoning effort is a budget.** Default to medium/high. Use the maximum only for a named hard
   problem (a cryptographic protocol, a security review), never as the session default.
3. **Never poll.** No sleep loops, no repeated `gh pr checks` / `gh run view` / terminal reads to
   see whether something finished. Start one blocking wait in the background that wakes you once,
   or end the turn. Waiting must cost nothing.
4. **Fail locally, not in CI.** Before any push run the gate on `dev` under the shared lock:
   `flock -o /tmp/tarkov-build.lock bash -c 'scripts/build.sh && scripts/test.sh'`. A compile error
   found by CI costs a 13-minute round trip plus every context re-read while waiting.
5. **Batch CI.** `main` requires a branch to be up to date before merging, so landing N related PRs
   one by one costs N extra full CI runs. Merge green branches into one integration PR and run CI
   once.
6. **Working product before hardening.** A feature is done when it is reachable in the running app,
   works end to end on real data, and looks like `docs/design/v2`. Do not loop
   audit → repair → re-audit on code nothing calls. One review per PR; fix its findings in that PR.
7. **Check the budget before a wave.** Look at usage before starting multiple agents; stop around
   80% of the weekly limit and tell Clayton.
8. **Clean up as you go.** Exit finished agent sessions and delete `bin`/`obj` in merged worktrees.
   On 2026-09-16 the `dev` disk filled, Orca crashed, and every running agent died with it.

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
| How fast is it over a raid, and what is guarded? | `docs/PERFORMANCE.md` |

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
