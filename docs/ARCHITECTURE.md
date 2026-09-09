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

Startup opens SQLite and applies hand-written migrations, immediately loads cached records, starts the UI, evaluates staleness, and refreshes in the background. Refreshes write a transactionally normalized snapshot and publish an application event.

A user scan captures visible pixels into memory, detects a context, obtains OCR/icon candidates, resolves canonical item or extract IDs, and only then invokes recommendation/economy services. Capture bytes are discarded by default.

Game logs and screenshot filenames are independent, evidence-based inputs to the raid state. Screenshot filenames update only the player's last-known position and always carry freshness. The strategy engine consumes public/static map inputs and never consumes enemy observations.

## Cross-platform contract

Linux must build and test all domain, application, data, recognition, simulator, and demo behavior. Windows-specific code is guarded behind interfaces and runtime OS checks. The self-contained `win-x64` publish is produced on Linux and proven in the Windows VM with synthetic permitted inputs.
