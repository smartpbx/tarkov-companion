# Data sources

Verified 2026-09-10.

## Primary structured data

Runtime source: `https://json.tarkov.dev`.

The live endpoint catalog reports:

- `/{gameMode}/items`
- `/{gameMode}/maps`
- `/{gameMode}/tasks`
- `/{gameMode}/hideout`
- `/{gameMode}/traders`
- `/{gameMode}/crafts`
- `/{gameMode}/barters`
- `/{gameMode}/prices/{itemId}`
- `/pvp-season/info`
- `/status`

Modes: `regular`, `pve`, `pvp-season`. English is the initial application language. Endpoints with translation envelopes are normalized through one translation applier.

Static datasets default to a 9-hour stale-while-revalidate window. Viewed/scanned price data defaults to a 10-minute window. Clients use bounded timeouts, cancellation, at most three attempts with jittered exponential delay, request deduplication, ETag/Last-Modified where present, and valid stale cache on failure.

### Reviewed extract supplements

The primary map payload is supplemented only for measured current extract omissions under ADR
0012. The bounded 2026-09-15 set contains nine name/faction/coordinate facts across five maps,
with a pinned MIT-licensed coordinate reference and current EFT Wiki list corroboration. It is
compiled into Infrastructure, performs no additional runtime request, retains per-row
provenance, and yields whenever the primary publishes the same map-scoped normalized name. See
`docs/research/EXTRACT_CATALOG_COVERAGE.md` for the reproducible sweep and exclusions.

## Map configuration and assets

Map geometry/configuration may be derived from current `the-hideout/tarkov-dev` data with provenance. Third-party map imagery is optional per-map data and cannot be treated as project-owned. When its license is not distribution-compatible, the app must download the original asset into the user's cache and expose attribution rather than embed a derivative.

## Optional player progress

TarkovTracker integration is optional, bearer-token-based, read-only, and never required for core operation. Its supported surface is limited to canonical `https://api.tarkovtracker.org/token` and `/progress` GET requests documented at TarkovTracker revision [`443d9fd73f0f88cac1623206fe79ba122ab9b1fb`](https://github.com/tarkovtracker-org/TarkovTracker/blob/443d9fd73f0f88cac1623206fe79ba122ab9b1fb/docs/API.md) and the pinned [OpenAPI source](https://github.com/tarkovtracker-org/TarkovTracker/blob/443d9fd73f0f88cac1623206fe79ba122ab9b1fb/workers/api-gateway/src/openapi.ts). It imports only task and objective progress after explicit preview; no external writes, team access, player metadata, hideout data, uploads, or polling occur in v1.

On Windows, the disconnected integration is available by default when per-user protected storage is available. `TARKOV_COMPANION_TARKOVTRACKER_ENABLED=false` (or `0`) is an explicit opt-out. Offline mode and unavailable protected storage disable Connect and Refresh, and composition or status inspection never performs network access. A user must explicitly Connect to validate and store a mode-scoped token, then explicitly Refresh; an optional foreground caller is rate-gated to no faster than 60 seconds.

## Clean-room references

RatScanner, TarkovMonitor, Tarkov Nexus tools, and eft-ammo may inform observable behavior or UX validation only. Their source, data tables, assets, wording, and implementation are not copied.
