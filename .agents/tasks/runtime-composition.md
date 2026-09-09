# Agent H — runtime composition and live application state

Work in a fresh Orca worktree from current `origin/main`. Read `AGENTS.md`, the autonomous handoff, `docs/reviews/ARCHITECTURE_REVIEW.md`, and `docs/reviews/RECOGNITION_REVIEW.md` first. Commit the completed work and report the commit hash and verification evidence.

## Goal

Turn the current library collection and static Avalonia shell into a composed application. The normal executable and `--demo` must use the same service-backed ViewModels; demo mode substitutes deterministic fixture adapters rather than hardcoded UI claims.

## Ownership

- composition/startup/configuration/logging under `src/TarkovCompanion.App/**`
- Application runtime coordinators outside the recognition-specific folders, under `src/TarkovCompanion.Application/**`
- missing SQLite repositories/runtime persistence under `src/TarkovCompanion.Infrastructure/Persistence/**`
- runtime-composition and UI-binding tests under `tests/**`
- relevant architecture/database/testing docs

Coordinate with the recognition remediation through existing interfaces. Do not implement or select the production OCR backend in this task. Do not rewrite map-specific controls being built in the tarkov.dev maps worktree; consume their services later through small interfaces.

## Required work

1. Add a real `Microsoft.Extensions.DependencyInjection` composition root. Resolve platform app-data paths, initialize a persistent SQLite database, apply migrations, register HTTP/cache/repositories, profile/intelligence/raid/strategy services, and logging. Windows-only implementations stay behind existing interfaces and are selected only on Windows.
2. Implement an async startup coordinator: load usable cached/local state first, show honest availability/freshness, then run bounded stale refresh in the background. Honor `TARKOV_COMPANION_OFFLINE=1` and never make offline startup depend on network.
3. Replace fabricated status/evidence strings with observable runtime state. Existing XAML may remain visually similar, but user-visible counts, prices, confidence, age, map/position, and scan status must come from service state or explicitly say unavailable/demo fixture. Wire controls/commands needed to exercise item search, sync, profile context, and scan dispatch.
4. Make `--demo` run deterministic fixture-backed services through the same ViewModels/composition path. Do not use `CreateFoundationDemo` as a parallel fake-data application.
5. Implement `IRaidHistoryService` over SQLite, including CSV export, and connect raid state transitions/scan events through a small application coordinator. Never persist captured pixels.
6. Wire `DiagnosticCommandChannel.Scan` to an injected scan-use-case abstraction. If recognition work is not yet present on the branch, define/consume the narrowest contract and provide an honest unavailable result rather than a static `scan-requested` success. Preserve token/auth/path hardening.
7. Update self-test to report actual database/cache/data/OCR-provider/diagnostic state rather than hardcoded booleans. Keep self-test network-free.
8. Add application-level tests proving persistent DB startup, offline restart from cache, fixture/demo composition, honest unavailable UI state, real item search through a ViewModel, raid-history persistence/export, and diagnostic scan delegation.

## Constraints

- No fabricated user-facing telemetry or evidence.
- No synchronous network or heavy DB work on the UI thread.
- Cancellation-aware bounded background work and deterministic shutdown.
- Preserve safety boundary and keep desktop-capture fallback opt-in/off by default.
- Keep edits surgical. If a contract needed from the recognition worker is unavailable, leave one clearly documented integration seam rather than duplicating recognition logic.

## Acceptance

- Normal and demo executables build through one composition root.
- Service implementations are constructed and exercised at runtime.
- Offline mode is real and tested across a new service-provider/database instance.
- UI/service tests contain no hardcoded evidence masquerading as observations.
- Build has zero warnings; full tests pass.
