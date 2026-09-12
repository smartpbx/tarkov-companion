# Tarkov Companion pause handoff

Paused after the second workstation hard lockup on 2026-09-10. Read
`AGENTS.md` before doing anything: no local .NET, debugger, Docker, Windows VM,
or local Orca worker/sub-agent process is permitted for this repository.

## Verified integrated baseline

- Local repo: `/home/cmannerow/Nextcloud/Documents/programming/tarkov-companion`
- Private remote: `https://github.com/smartpbx/tarkov-companion`
- Pushed `main`: `a75b1e5`
- GitHub Actions run `34523192334`: Linux and Windows-publish jobs green
- Last local verification performed before the permanent ban: zero-warning
  build, 225 runnable tests passed, five native-OCR tests skipped on Linux,
  safety/secret/license audits passed, and the 300-entry Windows archive passed
  integrity and legal-inventory checks.

Integrated work includes runtime composition, production OCR wiring,
tarkov.dev-only maps with selectable defaults and interactive overlays, local
quest progress, quest/map UI, project import/export with undo, optional
GET-only TarkovTracker import, and fail-closed dependency licensing/packaging.

## Preserved unfinished simulator work

- Worktree: `/home/cmannerow/orca/workspaces/tarkov-companion/simulator-smoke-remediation`
- Branch: `smartpbx/simulator-smoke-remediation`
- Stopped dispatch: `ctx_121abf52bce5`
- State: the worker terminal is stopped; changes are uncommitted and must not be
  discarded or the worktree deleted.
- Work underway: PNG-file capture, structured diagnostic results, persisted
  runtime evidence, and production scan-path plumbing for behavioral smoke.

Inspect this worktree with Git-only commands before continuing. Do not restart
its local agent or bootstrap/build/test inside it. Continue through direct file
edits, push a small branch/PR, and let GitHub Actions perform validation. If a
required check cannot run in CI, prepare the remote development container
deliberately after verifying its checkout and current load; never fall back to
the workstation.

## Fastest safe completion path

1. Preserve `main` as the known-green baseline.
2. Review and finish the existing simulator work in small, reviewable commits
   without local execution.
3. Push the worker branch and use GitHub Actions for each test-shaped checkpoint.
4. Integrate only green commits; avoid repeating full release audits after every
   edit when a targeted CI job can prove the changed behavior.
5. Keep real-EFT validation explicitly deferred. If Windows behavioral smoke
   cannot run on GitHub-hosted Windows, stop and ask Clayton for a remote-safe
   Windows target rather than starting the local VM.

## Product rules that remain locked

- Production maps come only from tarkov.dev/upstream assets. Never invent or
  image-generate production maps.
- Prefer the interactive map variant and persist a per-location default.
- Marker, extract, route, quest, label, floor, and prediction overlays remain
  independently hideable/highlightable.
- The companion remains external and read-only relative to Escape from Tarkov;
  all anti-cheat and source-honesty rules in `AGENTS.md` remain mandatory.

## Shared runbook

The durable cross-tool note is
`/home/cmannerow/Documents/ObsidianVault/Obsidian Vault/30 Resources/Apps/Tarkov Companion.md`.
