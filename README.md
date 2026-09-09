# Tarkov Companion

Tarkov Companion is a local-first Windows second-screen app for Escape from Tarkov. It turns user-triggered screen captures and normal game-produced files into explainable item, raid, map, ammunition, key, quest, hideout, event, loadout, and economy guidance. The source repository is private at <https://github.com/smartpbx/tarkov-companion>.

The app is deliberately external and read-only. It does not read game memory, inject code, hook rendering, inspect game traffic, send gameplay input, track enemies, automate the flea market, or draw over the game window. Traffic views are educational predictions based on public map knowledge—not live detections.

## Current build state

The v1 build is in active development. See [BUILD_STATUS](docs/BUILD_STATUS.md) for verified progress and [LIVE_EFT_VALIDATION](docs/LIVE_EFT_VALIDATION.md) for the only validation that may remain after simulator and Windows VM testing.

## Build

Requirements: .NET SDK 10.0.401 or a compatible 10.0 feature-band patch.

```bash
./scripts/build.sh
./scripts/test.sh
./scripts/package-windows.sh
```

Linux demo mode:

```bash
dotnet run --project src/TarkovCompanion.App -- --demo
```

Windows self-test:

```powershell
TarkovCompanion.exe --self-test --output self-test.json
```

## Data and attribution

Structured game data is sourced from [json.tarkov.dev](https://json.tarkov.dev). Third-party source, asset, and package details are recorded in [DATA_SOURCES](docs/DATA_SOURCES.md), [LICENSING](docs/LICENSING.md), and [THIRD_PARTY_NOTICES](docs/THIRD_PARTY_NOTICES.md).

Escape from Tarkov and related marks are property of Battlestate Games. This independent project is not affiliated with or endorsed by Battlestate Games.
