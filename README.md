# Tarkov Companion

Tarkov Companion is a local-first Windows second-screen app for Escape from Tarkov. It turns user-triggered screen captures and normal game-produced files into explainable item, raid, map, ammunition, key, quest, hideout, event, loadout, and economy guidance. The source repository is private at <https://github.com/smartpbx/tarkov-companion>.

The app is deliberately external and read-only. It does not read game memory, inject code, hook rendering, inspect game traffic, send gameplay input, track enemies, automate the flea market, or draw over the game window. Traffic views are educational predictions based on public map knowledge—not live detections.

## Current build state

The v1 build is in active development. See [BUILD_STATUS](docs/BUILD_STATUS.md) for verified progress and [LIVE_EFT_VALIDATION](docs/LIVE_EFT_VALIDATION.md) for the only validation that may remain after simulator and Windows VM testing.

## Install and run on Windows

Download `TarkovCompanion-v1.0.0-win-x64.zip` from the Windows verification workflow's
artifacts, then:

1. Right-click the downloaded zip, choose Properties, tick **Unblock**, and apply. Windows
   marks downloaded archives, and without this SmartScreen blocks the extracted application.
2. Extract the archive anywhere you like. It is self-contained, so no .NET runtime is needed.
3. Install the [Microsoft Visual C++ 2015-2022 x64 runtime](https://aka.ms/vs/17/release/vc_redist.x64.exe)
   if it is not already present. The bundled Tesseract and Leptonica libraries depend on it,
   and local OCR stays unavailable without it. A machine that runs Escape from Tarkov almost
   certainly already has it.
4. Run `TarkovCompanion.exe`. The first launch creates `%LOCALAPPDATA%\TarkovCompanion`,
   downloads the current tarkov.dev catalogs, and takes roughly ten seconds on a normal
   connection. Everything after that is local.

The application is unsigned, so SmartScreen may still warn on first run.

To check an installation without opening a window:

```powershell
TarkovCompanion.exe --self-test --output self-test.json
```

Startup and crash detail is appended to `%LOCALAPPDATA%\TarkovCompanion\Logs\startup.log`.

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

Build and test work runs in GitHub Actions. The Windows verification workflow packages the
win-x64 archive, launches it on a hosted Windows runner, and publishes the self-test reports
and a screenshot of the running application alongside the package.

## Data and attribution

Structured game data is sourced from [json.tarkov.dev](https://json.tarkov.dev). Third-party source, asset, and package details are recorded in [DATA_SOURCES](docs/DATA_SOURCES.md), [LICENSING](docs/LICENSING.md), and [THIRD_PARTY_NOTICES](docs/THIRD_PARTY_NOTICES.md).

Escape from Tarkov and related marks are property of Battlestate Games. This independent project is not affiliated with or endorsed by Battlestate Games.
