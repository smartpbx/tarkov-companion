# Agent D — raid, map, strategy, and Windows observations

Worktree: a dedicated Orca top-level worktree created from `origin/main`; use the exact current working directory reported by Orca.

Read root `AGENTS.md` before editing. Do not modify outside this worktree. Commit the finished work with a focused message.

## Ownership

- `src/TarkovCompanion.Platform.Windows/**`
- new files under `src/TarkovCompanion.Application/Services/Raids/**`, `Maps/**`, and `Strategy/**`
- `assets/strategy/**`
- `fixtures/logs/**`, `fixtures/maps/**`, `fixtures/screenshot-filenames/**`
- raid/map/platform-specific files under `tests/TarkovCompanion.UnitTests/**` and `tests/TarkovCompanion.WindowsSmokeTests/**`
- `docs/MAPS.md`, `docs/STRATEGY.md`, `docs/WINDOWS.md`

Do not alter recognition/UI/data-source files or frozen Core contracts without coordinator approval.

## Deliverables

- Tolerant fixture-driven log parser and evidence-based raid state service.
- Screenshot watcher and tested filename/position integration, freshness, quaternion heading, floor, transform validation.
- Map data/cache-facing services, active extract state, and honest missing-transform behavior.
- Generic early/mid/late predicted traffic field, risk panel data, and rotation flows with explicit non-live disclaimer.
- Navigation-graph route planner for Fastest/Safest/Quest/Loot/AvoidPvP with honest incomplete-graph fallback.
- Ordinary Windows process/window discovery, DPI-aware GDI/desktop capture fallback, RegisterHotKey receiver, monitor enumeration, EFT path discovery, log/screenshot watchers, and DPAPI-backed secrets.
- Production ignores simulator unless DeveloperMode is explicit. Never send input.

## Acceptance

- All P/Invoke is Windows-gated and Linux build remains green.
- No process memory, injection, hooks, packet capture, input generation, enemy objects, or overlay code.
- Spawn influence decays and late extract attraction increases in tests.
- Owned projects build with zero warnings; tests pass where available.
- Commit and report commit hash, test counts, and VM-only items.
