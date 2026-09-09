# Testing and synthetic smoke validation

All automated validation is external to Escape from Tarkov. The simulator uses generic artwork, fixture logs, and filename-only screenshot markers. It never reads game memory or traffic, sends input, injects code, or represents predictions as detections.

## Linux build and test

From the repository root:

```bash
./scripts/build.sh
./scripts/test.sh
```

Run the complete deterministic demo raid without starting a window:

```bash
dotnet run --project src/TarkovCompanion.App -- \
  --demo --headless \
  --demo-fixture fixtures/simulator/full-raid.json \
  --output /tmp/tarkov-companion-demo.json
```

The report must include the ordered `map`, `position`, `item`, `extracts`, `container`, and `raid-end` events, and must set `usesLiveDetection` to `false`. Plain `--demo` still launches the visual Linux demo.

Run the headless application self-test:

```bash
dotnet run --project src/TarkovCompanion.App -- \
  --self-test --output /tmp/tarkov-companion-self-test.json
```

The self-test uses a disposable SQLite database and makes no network request. Its JSON covers database migration, cache schema, FTS search, platform/runtime, the configured `json.tarkov.dev` provider, writable paths, and safety invariants. Missing EFT install, log, or screenshot paths are reported as state and do not fail an otherwise healthy offline self-test.

## Simulator

The canonical scenarios, in deterministic order, are:

1. `RaidStart_Customs`
2. `Inspect_GraphicsCard`
3. `Inspect_AmmoPack`
4. `Inspect_Key`
5. `Inspect_Consumable`
6. `ExtractList_Customs`
7. `Container_Mixed`
8. `Flea_VisibleListings`
9. `PositionUpdate`
10. `RaidEnd`

List them as JSON:

```bash
dotnet run --project src/TarkovCompanion.EftSimulator -- --list-scenarios
```

The simulator refuses to start or emit files without explicit Developer Mode. A headless fixture example is:

```bash
dotnet run --project src/TarkovCompanion.EftSimulator -- \
  --developer-mode \
  --emit-fixtures-only \
  --scenario PositionUpdate \
  --log-root /tmp/tarkov-sim/logs \
  --screenshot-root /tmp/tarkov-sim/screenshots \
  --state-output /tmp/tarkov-sim/state.json
```

The screenshot files are intentionally zero-byte filename markers. They exercise filename watchers/parsers without persisting captured screen pixels.

## Developer diagnostic channel

The channel exists only when the app receives both `--developer-mode` and `--diagnostic-channel <directory>`. Set `TARKOV_COMPANION_DIAGNOSTIC_TOKEN` to an ephemeral value of at least 32 characters before launch. Without explicit Developer Mode the same arguments do not create or monitor a channel.

Tests atomically place `<id>.command.json` files in `<directory>/commands`; responses appear in `<directory>/responses`. The only accepted commands are `scan` and `scenario`, identifiers allow only ASCII letters, digits, hyphens, and underscores, and scenario events carry a label only. This channel records test events; it cannot invoke processes, arbitrary paths, keyboard/mouse input, purchases, inventory actions, aiming, or combat.

Example command:

```json
{
  "id": "scenario-1",
  "command": "scenario",
  "token": "use-an-ephemeral-32-character-or-longer-value",
  "scenario": "Inspect_GraphicsCard"
}
```

## Windows smoke harness

Prerequisites:

- Windows 10 or newer in an interactive desktop session.
- Published `TarkovCompanion.exe` and `TarkovCompanion.EftSimulator.exe` paths.
- Write access to the selected work and report directories.
- No EFT installation and no network connection are required.

Run in PowerShell 7 or Windows PowerShell 5.1:

```powershell
./scripts/windows-smoke.ps1 `
  -AppPath C:\TarkovCompanion\TarkovCompanion.exe `
  -SimulatorPath C:\TarkovCompanion\TarkovCompanion.EftSimulator.exe `
  -OutputPath C:\TarkovCompanion\windows-smoke-report.json
```

The harness validates the headless self-test, starts only processes it later stops, launches all ten simulator scenes, asserts generated logs/filename markers and safety flags, sends scan/scenario commands through the file channel, then relaunches the demo with offline mode set. It does not use screen coordinates, pixel assertions, UI automation, or synthetic gameplay input. The report is JSON and the process exits nonzero on the first failed assertion.
