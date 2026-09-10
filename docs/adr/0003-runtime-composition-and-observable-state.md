# ADR 0003: Single runtime composition and observable state

Status: Accepted — 2026-09-10

## Context

The foundation supplied domain and infrastructure services, but the executable shell presented independently constructed sample values. That made normal startup, demo behavior, diagnostics, local cache availability, and shutdown diverge from the code paths exercised by the service layer.

## Decision

Use `AppComposition` as the single dependency-injection root for the normal GUI, demo GUI, diagnostic command channel, and self-test. Initialize the persistent database and local profile before publishing an observable `ApplicationRuntimeSnapshot`, then perform stale network refreshes as bounded background work. Offline mode rejects HTTP at the handler boundary and never schedules refresh.

Normal and demo modes resolve the same ViewModels and application use cases. Demo mode substitutes only deterministic local seed/scan adapters. Production scanning remains behind `IScanAdapter`; until recognition provides a production implementation, the normal application and diagnostic channel return an explicit unavailable result rather than claiming success.

Persist evidence-based raid transitions and successful scan events through `IRaidHistoryService`, using SQLite summary/event tables and UTC timestamps. Never persist captured pixels. Compose the tarkov.dev interactive map services into the same provider without changing their rendering or asset-license boundary.

## Consequences

Displayed data, freshness, raid state, map state, and scan evidence now originate in runtime state or repositories and can explicitly degrade to unavailable. Offline restarts can use normalized cache data without contacting the network, diagnostics exercise the same scan use case as the UI, and shutdown can cancel and await background work before provider disposal. Production OCR selection remains an independent integration task at the narrow scan-adapter seam.
