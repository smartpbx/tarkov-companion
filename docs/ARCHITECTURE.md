# Architecture

## Dependency direction

```text
App ────────────────┐
Platform.Windows ───┼──> Application ──> Core
Infrastructure ─────┘         │
                              └── contracts implemented by outer layers
EftSimulator ─────────────> App/Application test seams
```

`Core` contains immutable domain records, deterministic calculations, and boundary interfaces that do not require platform or I/O packages. `Application` coordinates use cases. `Infrastructure` owns HTTP, SQLite, caching, OCR/image recognition, and optional external progress import. `Platform.Windows` owns ordinary Windows window discovery, capture, hotkeys, monitors, path discovery, watchers, and secrets. `App` owns Avalonia views and ViewModels only.

## Runtime flow

`AppComposition` is the single composition root for the normal GUI, demo GUI, diagnostics, and self-test. It resolves the application-data directories, persistent SQLite database, HTTP/cache and normalized repositories, profile/intelligence/raid/strategy services, interactive map services, logging, runtime coordinators, scan seam, and service-backed ViewModels. Platform-specific implementations are registered only on Windows.

Startup applies the hand-written migrations, seeds deterministic local data only in demo mode, loads the local profile and usable cached records, and publishes an observable runtime snapshot before starting any refresh. When local data is stale, the coordinator performs a bounded forced refresh on a background task; each successful endpoint remains transactionally independent. `TARKOV_COMPANION_OFFLINE=1` substitutes a rejecting HTTP handler, skips refresh, and labels either the existing cache or its absence honestly.

The UI consumes runtime snapshots and repositories rather than constructing a second fake application model. Normal and demo modes use the same commands and ViewModels. Demo mode changes only the registered fixture adapter and deterministic seed, while unavailable data, map state, position, or scans are presented as unavailable. Application shutdown cancels and awaits startup/map work before disposing the service provider.

A user scan captures visible pixels into memory, detects a context, obtains OCR/icon candidates, resolves canonical item or extract IDs, and only then invokes recommendation/economy services. Capture bytes are discarded by default.

The executable currently composes `IScanUseCase` through an `IScanAdapter` seam. Demo mode registers a deterministic adapter that resolves a seeded item through the real item and recommendation services; normal mode registers an honest unavailable adapter until the production recognition work provides an implementation. The authenticated developer diagnostic channel invokes this same use case.

Game logs and screenshot filenames are independent, evidence-based inputs to the raid state. Screenshot filenames update only the player's last-known position and always carry freshness. The strategy engine consumes public/static map inputs and never consumes enemy observations.

`RaidActivityCoordinator` records raid starts, evidence transitions, positions, extracts, successful scans, and raid completion through `IRaidHistoryService`. History stores structured JSON event payloads and summary rows only; captured pixels are never persisted.

## Cross-platform contract

Linux must build and test all domain, application, data, recognition, simulator, and demo behavior. Windows-specific code is guarded behind interfaces and runtime OS checks. The self-contained `win-x64` publish is produced on Linux and proven in the Windows VM with synthetic permitted inputs.
