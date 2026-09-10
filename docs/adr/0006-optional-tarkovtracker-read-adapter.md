# ADR 0006: Optional TarkovTracker read adapter

Status: Accepted — 2026-09-10

## Context

ADR 0004 makes exact local profile, mode, and generation state canonical. ADR 0005 gives every imported snapshot a preview, explicit conflict decisions, atomic apply and journal, unresolved evidence, and undo. Some users already record task progress in TarkovTracker, but making that service authoritative would break offline operation and would fabricate ordering because its public progress response has no per-field edit timestamps or Tarkov Companion generation.

The upstream contract was re-verified at TarkovTracker revision `443d9fd73f0f88cac1623206fe79ba122ab9b1fb` using only the official [API integration documentation](https://github.com/tarkovtracker-org/TarkovTracker/blob/443d9fd73f0f88cac1623206fe79ba122ab9b1fb/docs/API.md) and [OpenAPI source](https://github.com/tarkovtracker-org/TarkovTracker/blob/443d9fd73f0f88cac1623206fe79ba122ab9b1fb/workers/api-gateway/src/openapi.ts). Those sources define the canonical `https://api.tarkovtracker.org` origin, mode-scoped token prefixes, `GP` progress-read permission, `GET /token`, `GET /progress`, weak ETags, quota headers, `Retry-After`, and a recommendation not to poll faster than once per minute.

## Decision

Add a clean-room, optional read adapter whose public HTTP interface contains exactly token validation and progress fetch. It constructs only canonical HTTPS `/token` and `/progress` URIs and only `GET` requests. Its production handler has automatic redirects disabled; every 3xx response is rejected without exposing its destination or reusing the authorization header. The adapter sends a validated descriptive 5–200 character User-Agent, bounds response size and JSON depth, uses a hard request timeout with distinct caller cancellation, and emits redacted typed failures for 401, 403, 429, 5xx, transport, timeout, redirect, and invalid response cases.

Connect first validates the exact `PVP_`, `PVE_`, or `SZN_` prefix for the active local mode, calls `GET /token`, requires the returned mode to match, requires the returned token metadata to agree without including the value in diagnostics, and requires `GP`. Only after validation does it save the token. Secret persistence is behind the narrow `IIntegrationSecretStore`; Windows implements it with current-user DPAPI and an unavailable implementation fails closed elsewhere. Tokens are never written to SQLite, logs, exceptions, exports, UI status, telemetry, URLs, or test fixtures. Disconnect deletes the exact profile/mode/generation secret locally and clears the in-memory snapshot session.

The disconnected feature is available by default on Windows when protected storage reports available. `TARKOV_COMPANION_TARKOVTRACKER_ENABLED=false` or `0` is an explicit opt-out. Offline mode disables its network access, and unavailable protected storage disables the feature. Runtime composition and status inspection are local operations and never contact TarkovTracker; only explicit Connect and Refresh actions perform network I/O. Local Disconnect remains available wherever protected storage is available, including while opted out or offline.

Progress uses `If-None-Match`, retains the last in-memory snapshot for a `304`, reads quota limit/remaining/reset headers, honors `Retry-After`, pauses at a reported zero quota until reset, and backs off bounded transient failures. There is no startup or background polling. Manual refresh is explicit; a foreground refresh entry point exists for a foreground caller and rejects refreshes less than 60 seconds apart.

Only supported task fields (`id`, `complete`, optional `failed`, `invalid`) and objective fields (`id`, `complete`, optional nonnegative decimal `count`, `invalid`) are mapped. Account/profile metadata, hideout state, team data, and unknown fields are ignored. Duplicate, malformed, wrong-mode, or otherwise invalid documents fail without a preview. Unknown catalog IDs and upstream-invalid or contradictory records are retained as unresolved evidence and never applied.

Every successful fetch becomes a source-honest `TarkovTracker` import snapshot with fetch time, pinned contract revision, payload hash, and an explicit statement that fetch time is not edit time. It enters the existing source-neutral preview and reconciliation planner. Nothing changes until the user confirms all conflicts and Apply; database state, import metadata, decisions, unresolved evidence, inverse journal, and revision commit atomically. Undo uses the same append-only boundary. The existing migration 0006 already stores source-neutral import metadata, so this decision needs no new migration and does not edit an applied migration.

## Consequences

TarkovTracker is a convenience import source, never a prerequisite or authority. Users keep full offline quest tracking, can inspect conflicts before local mutation, and can reverse an applied batch without contacting TarkovTracker. The adapter cannot write external progress because it has no mutation method, cannot access teams because it has no team route, and cannot silently follow a credential-bearing redirect.

The limitations are intentional: no external writes, uploads, telemetry, game access, traffic inspection, automation, team endpoints, player identity import, hideout import, third-party backup import, source edit timestamps, cross-device token storage, scheduled polling, or automatic apply. Protected storage is Windows/current-user only in Stage 5; other platforms show the integration as unavailable unless a future platform adapter is explicitly designed and reviewed. ETags and quota state are session-local, so an application restart performs no automatic refresh and begins a later explicit refresh without the prior validator.
