# Data sources

Verified 2026-09-09.

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

## Map configuration and assets

Map geometry/configuration may be derived from current `the-hideout/tarkov-dev` data with provenance. Third-party map imagery is optional per-map data and cannot be treated as project-owned. When its license is not distribution-compatible, the app must download the original asset into the user's cache and expose attribution rather than embed a derivative.

## Optional player progress

TarkovTracker integration is optional, bearer-token-based, read-only, and never required for core operation. No external writes occur in v1.

## Clean-room references

RatScanner, TarkovMonitor, Tarkov Nexus tools, and eft-ammo may inform observable behavior or UX validation only. Their source, data tables, assets, wording, and implementation are not copied.
