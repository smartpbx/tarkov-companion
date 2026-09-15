# Testing and synthetic smoke validation

All automated validation is external to Escape from Tarkov. The simulator uses generic artwork, fixture logs, and filename-only screenshot markers. It never reads game memory or traffic, sends input, injects code, or represents predictions as detections.

## Where to run it

**Not on the workstation.** It has 31 GB and routinely sits at ~7 GB free, and repeated full
suites there have crashed the apps somebody was using. `AGENTS.md` makes that a rule rather
than a preference, and this document used to open by telling you to break it.

In order of preference:

1. **GitHub Actions.** `ci.yml` and `windows-verify.yml` run the whole suite on every pull
   request; pushing a branch is the cheapest way to get a full answer.
2. **CT 114 on Proxmox**, which is where the fast loop lives. The .NET 10 SDK is at
   `/root/.dotnet` and is *not* on `PATH`:

   ```bash
   ssh proxmox 'pct exec 114 -- bash -lc "
     cd /root/repos/tarkov-companion
     export PATH=/root/.dotnet:\$PATH DOTNET_ROOT=/root/.dotnet
     dotnet build -c Release && dotnet test -c Release --no-build"'
   ```

   A build is about fifteen seconds and the whole suite about twenty, which is why every
   change in this repository is verified there before it is pushed.

Building on Linux works for every project including the Windows target, because
`EnableWindowsTargeting` is set. Running the Windows package is a different question, and
`WINDOWS_VERIFICATION.md` answers it.

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

The self-test builds the production composition root with networking forcibly disabled and initializes the persistent database selected by the application's data-path policy. Its JSON reports actual database readiness/path, normalized cache count and availability, profile loading, configured `json.tarkov.dev` services, production OCR-provider presence, diagnostic configuration, writable paths, platform/runtime, and safety invariants. An empty cache and an intentionally absent production OCR provider are nonrequired unavailable states; missing EFT install, log, or screenshot paths are reported as state and do not fail an otherwise healthy offline self-test.

## Runtime-composition coverage

`RuntimeCompositionTests` constructs the same dependency-injection graph used by the executable with isolated data roots and deterministic HTTP handlers. It proves that:

- demo mode persists its seed and exercises item search and scan commands through the shared `MainWindowViewModel`;
- a normal offline first run exposes unavailable data and scan state without invented telemetry;
- a newly constructed offline provider reloads a previously normalized cache without making an HTTP request; and
- evidence transitions and successful scan events persist through SQLite and export as CSV.

Diagnostic-channel tests inject a scan-use-case stub and assert authenticated delegation as well as honest unavailable results. UI tests resolve `MainWindowViewModel` from the real service provider instead of using a parallel static demo constructor.

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

Durable state is read from the demo database, read-only, through the `e_sqlite3.dll` in the package directory, which loads under either PowerShell; the managed `Microsoft.Data.Sqlite` assembly in the package is net10.0 and does not load in Windows PowerShell 5.1. The harness waits for a migrated schema and for the raid its launch opened before taking the scan-row baseline, and waits for each scan's committed `raid_events` row before the next scan, because raid history is written behind a queue after the diagnostic response. The simulator's files are not ingested by the packaged app; the scans come from the demo fixture adapter.
