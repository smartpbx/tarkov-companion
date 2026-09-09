# Agent E — Avalonia second-screen UI

Worktree: a dedicated Orca top-level worktree created from `origin/main`; use the exact current working directory reported by Orca.

Read root `AGENTS.md` and `docs/DESIGN_SYSTEM.md` before editing. Do not modify outside this worktree. Commit the finished work with a focused message.

## Ownership

- `src/TarkovCompanion.App/Views/**`
- `src/TarkovCompanion.App/ViewModels/**`
- `src/TarkovCompanion.App/Controls/**`
- `src/TarkovCompanion.App/Themes/**`
- `src/TarkovCompanion.App/Converters/**`
- presentation assets under `src/TarkovCompanion.App/Assets/**`
- UI-only tests added under `tests/TarkovCompanion.UnitTests/UI/**`

Do not edit `Program.cs`, infrastructure, Windows platform, or Core/Application business logic. Coordinate any required App bootstrap change.

## Deliverables

- Navigable Raid, Scanner, Items, Ammo, Keys, Flea, Quests, Hideout, Events, Loadout, History, and Settings pages.
- Status strip with EFT/map/raid/time/position/data/scan evidence and freshness.
- Map-first raid dashboard with pan/zoom/layers/filter controls, marker/heading, extracts, traffic disclaimer, strategy, route status, and last scan.
- Adaptive item card, ammo/key/event/loadout/history/reference presentations with realistic fixture/demo data.
- First-run wizard, diagnostics/settings, path/hotkey/monitor/privacy controls, confidence/age/error/empty states.
- Keyboard focus, DPI scaling, text labels beyond color, and no tiny critical text.

## Acceptance

- Maintain the cartographic instrument-panel direction; do not mimic EFT or produce generic SaaS cards.
- ViewModels invoke or represent application contracts; no domain rules in UI.
- `--demo` is visually useful on Linux.
- App builds with zero Avalonia/XAML warnings.
- Commit and report commit hash, implemented screens, and screenshot/render caveats.
