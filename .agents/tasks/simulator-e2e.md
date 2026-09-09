# Agent F — simulator, demo orchestration, self-test, and smoke harness

Worktree: a dedicated Orca top-level worktree created from `origin/main`; use the exact current working directory reported by Orca.

Read root `AGENTS.md` before editing. Do not modify outside this worktree. Commit the finished work with a focused message.

## Ownership

- `src/TarkovCompanion.EftSimulator/**`
- `src/TarkovCompanion.App/Services/Diagnostics/**`
- `src/TarkovCompanion.App/Program.cs` and `App.axaml.cs` only for headless self-test/demo wiring; do not change views/viewmodels
- simulator/replay-specific files under `tests/TarkovCompanion.IntegrationTests/**` and `tests/TarkovCompanion.WindowsSmokeTests/**`
- `fixtures/simulator/**`
- `scripts/windows-smoke.ps1`
- `docs/TESTING.md`

Do not alter recognition/data/Windows implementations; use contracts and deterministic fixture adapters.

## Deliverables

- Ten required named simulator scenarios with generic non-EFT artwork and deterministic command-line switching.
- Fake log and screenshot-filename output for configured paths.
- Safe local diagnostic command channel for test-triggered scan/scenario events; it must never send game input.
- `TarkovCompanion.exe --self-test --output <json>` headless report covering DB/cache/search/platform/provider/path state.
- Linux `--demo`/fixture raid replay spanning map, position, item, extracts, container, and raid end.
- PowerShell Windows smoke flow covering processes, self-test, simulator scenes, assertions, offline relaunch, and machine-readable report.
- Integration tests for a full synthetic raid.

## Acceptance

- Simulator is ignored outside explicit DeveloperMode.
- Smoke harness uses diagnostic IPC/files, not pixel-coordinate GUI or synthetic gameplay input.
- Self-test never exposes a dangerous game-control surface.
- Owned projects build with zero warnings; tests pass where available.
- Commit and report commit hash, test counts, and VM prerequisites.
