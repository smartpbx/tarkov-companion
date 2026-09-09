# Tarkov Companion pause handoff

Paused safely on 2026-09-09 so the workstation can reboot. The integrated `main` branch and every incomplete worker branch are pushed to the private GitHub repository.

## Integrated baseline

- local repo: `/home/cmannerow/Nextcloud/Documents/programming/tarkov-companion`
- private remote: `https://github.com/smartpbx/tarkov-companion`
- main at pause: `0b50b2a`
- last fully observed green CI before task-only commits: `670685d` (`34409264832`)
- first-wave result before remediation: 10 projects build with zero warnings; 114 tests pass; Linux and Windows-publish CI green; safety and secret scans green

## Paused Orca work

| Work | Task | Stopped dispatch | Worktree | Private checkpoint branch / commit | State at pause |
|---|---|---|---|---|---|
| tarkov.dev maps | `task_00b9d746e62a` | `ctx_605813216ced` | `/home/cmannerow/orca/workspaces/tarkov-companion/tarkovdev-maps` | `smartpbx/tarkovdev-maps` @ `ff21529` | WIP checkpoint; rebased through `3b53d2e`; guessed raster URL removed; build had passed during iteration, final tests/cleanup not completed |
| runtime composition | `task_44653c15be59` | `ctx_e8db187578f4` | `/home/cmannerow/orca/workspaces/tarkov-companion/runtime-composition` | `smartpbx/runtime-composition` @ `e57553e` | WIP checkpoint; composition/runtime/persistence scaffolding written; UI/startup integration and verification incomplete |
| recognition remediation | `task_87c68498a71e` | `ctx_9ac5223edc9e` | `/home/cmannerow/orca/workspaces/tarkov-companion/recognition-remediation` | `smartpbx/recognition-remediation` @ `0bd6693` | WIP checkpoint; Tesseract provider/dependency/licensing and domain refactor underway; scan orchestration/fixtures/tests incomplete |

The stopped tasks are intentionally marked blocked by Orca because the user requested a pause. No worktree should be deleted. Resume each task in its existing worktree with a new supervised worker attempt linked to the stopped dispatch; instruct the worker to inspect its WIP commit and continue, not restart.

## Product decisions already locked

- Production gameplay maps must come from tarkov.dev catalog/config and explicit upstream asset paths.
- Prefer the interactive variant; persist a user-selected default variant per EFT location.
- Markers, extracts, routes, labels, floors, filters, and predicted traffic are separate hideable/highlightable overlays.
- Never invent or image-generate a replacement production map. Synthetic geometry is simulator/test-only and not production-selectable.
- Permanent read-only/safety boundary in `AGENTS.md` remains mandatory.

## Resume order

1. Restart Orca and open this repository; read this file plus `AGENTS.md`.
2. Resume the three stopped workers in their existing worktrees from the checkpoint branches above.
3. Finish and integrate maps first, then rebase/finish runtime composition and recognition remediation against the latest `origin/main`.
4. Run full build/tests/audits and push `main`.
5. Run `.agents/tasks/release-license-remediation.md` after final dependencies settle.
6. Run `.agents/tasks/simulator-smoke-remediation.md` after runtime + recognition + maps are integrated.
7. Package the final win-x64 build, run the behavioral smoke harness in the local Windows 11 VM, and require a real JSON report before release.
8. Run the final independent Claude review and address release-blocking findings.

## Windows validation state

- dockur Windows 11 VM storage is persistent at `/home/cmannerow/.windows` and must not be deleted.
- host share is `/home/cmannerow/Windows`; the container mounts it at `/shared`.
- interactive RDP automation was proven: a harmless PowerShell probe returned a Windows 11 JSON result through an RDP-redirected drive.
- the VM may remain stopped until final packaging; starting/stopping the container does not delete the Windows disk.

## Durable cross-tool runbook

The canonical app note is `/home/cmannerow/Documents/ObsidianVault/Obsidian Vault/30 Resources/Apps/Tarkov Companion.md`; it records the repo, access method, safety boundary, map-source decision, and VM caution.
