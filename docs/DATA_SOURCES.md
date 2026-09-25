# Data sources

Verified 2026-09-16.

Local only (#292): Setup › Data & Privacy's switch, saved in `Config/network.json` with per-service switches for squad sharing, update checks, problem reports and TarkovTracker, is asked by every outbound client before it connects (`INetworkPolicy`); `TARKOV_COMPANION_OFFLINE=1` holds it on.

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

The `/items` payload is more than items. Beside `data.items` it carries `data.fleaMarket`, whose
`sellOfferFeeRate` and `sellRequirementFeeRate` were both 0.05 on 2026-09-19, and each item
carries `basePrice`, `lastOfferCount` and `buyFromTrader`. The Loot Scan's flea fee and
obtainability band are worked out from these; see `docs/LOOT_SCAN.md`.

Modes: `regular`, `pve`, `pvp-season`. English is the initial application language. Endpoints with translation envelopes are normalized through one translation applier.

Static datasets default to a 9-hour stale-while-revalidate window. Viewed/scanned price data defaults to a 10-minute window. Clients use bounded timeouts, cancellation, at most three attempts with jittered exponential delay, request deduplication, ETag/Last-Modified where present, and valid stale cache on failure.

### Reviewed extract supplements

The primary map payload is supplemented only for measured current extract omissions under ADR
0012. The bounded 2026-09-15 set contains eleven name facts across seven maps: nine have reviewed
world coordinates, two deliberately remain unplotted, and one retains an unknown faction. The
coordinate facts use pinned MIT-licensed SPT-DynamicMaps configurations and retained map/data
credits; permanent EFT Wiki revisions provide the current-list comparison. The supplement is
compiled into Infrastructure, performs no additional runtime request, retains per-row
provenance, and yields whenever the primary publishes the same map-scoped normalized name. See
`docs/research/EXTRACT_CATALOG_COVERAGE.md` for the method and exclusions and
`fixtures/extract-catalog/coverage-2026-09-15.normalized.json` for its retained deterministic
input. A checked 2026-09-23 embedded table also fills missing backpack, armor, carried-item,
and timed-window extract conditions.

### High-value loot-spawn locations

The `json.tarkov.dev` maps payload supplies loose-loot positions with candidate item IDs and
container positions with container-type IDs. Its item catalog supplies canonical item and value
metadata. These public structured facts are the primary source for the high-value model. Explicit
floors, spawn probability, respawn behavior, and container candidate contents are not published
there and remain unknown unless a separately reviewed, licensed curated source supplies the gap.

Runtime inputs use a versioned normalized-bundle contract with explicit provenance, licence,
timestamps, confidence, content hash, map/transform identity, precision, pool semantics, and
measured per-map coverage. The importer never derives or guesses a location from item metadata.
The production adapter composes exact mode-bound maps and items response documents with the
reviewed, hashed tarkov.dev map catalog. A publication retains the separate exact SHA-256 identities
for the raw maps response, language-applied maps view, raw items response, language-applied items
view, and map catalog in addition to its framed composite identity. Reviewed aliases currently
cover 16 source records across 13 runtime-supported canonical maps; the tutorial-only Ground Zero
record remains explicitly unmatched. Candidate membership remains unweighted, container contents
and probability remain unknown, and an unprojectable position is retained as map-only knowledge.
A restart-durable, bounded publication store is composed into desktop startup behind the same
application contract. Offline startup restores its last-known-good head without source I/O, and a
shared online refresh updates it from the exact current maps/items response documents. Silent
shrink remains refused; a legitimate wipe/removal requires an exact-head reviewed authorization
and leaves a bounded durable journal. The bundled manifest example is synthetic and test-only. See
[`LOOT_SPAWN_SOURCES.md`](LOOT_SPAWN_SOURCES.md) and ADR 0019 for the contract, measured 2026-09-16
input counts, validation, publication behavior, and remaining Raid-host gap.

## Map configuration and assets

Map geometry/configuration may be derived from current `the-hideout/tarkov-dev` data with provenance. Third-party map imagery is optional per-map data and cannot be treated as project-owned. When its license is not distribution-compatible, the app must download the original asset into the user's cache and expose attribution rather than embed a derivative.

## Optional player progress

TarkovTracker integration is optional, bearer-token-based, read-only, and never required for core operation. Its supported surface is limited to canonical `https://api.tarkovtracker.org/token` and `/progress` GET requests documented at TarkovTracker revision [`443d9fd73f0f88cac1623206fe79ba122ab9b1fb`](https://github.com/tarkovtracker-org/TarkovTracker/blob/443d9fd73f0f88cac1623206fe79ba122ab9b1fb/docs/API.md) and the pinned [OpenAPI source](https://github.com/tarkovtracker-org/TarkovTracker/blob/443d9fd73f0f88cac1623206fe79ba122ab9b1fb/workers/api-gateway/src/openapi.ts). It imports only task and objective progress after explicit preview; no external writes, team access, player metadata, hideout data, uploads, or polling occur in v1.

On Windows, the disconnected integration is available by default when per-user protected storage is available. `TARKOV_COMPANION_TARKOVTRACKER_ENABLED=false` (or `0`) is an explicit opt-out. Offline mode and unavailable protected storage disable Connect and Refresh, and composition or status inspection never performs network access. A user must explicitly Connect to validate and store a mode-scoped token, then explicitly Refresh; an optional foreground caller is rate-gated to no faster than 60 seconds.

## Clean-room references

RatScanner, TarkovMonitor, Tarkov Nexus tools, and eft-ammo may inform observable behavior or UX validation only. Their source, data tables, assets, wording, and implementation are not copied.

## Historical traffic and model snapshots

No production historical-traffic source or model is currently bundled. The governed source
inventory, consent and allowed-use rules, deterministic partition/build procedure, signed package
format, and exact compatibility behavior are defined in
[`docs/research/TRAFFIC_DATA.md`](research/TRAFFIC_DATA.md) and ADR 0017. Raw local feedback is
private by default and cannot be a distributable source row. A future reviewed aggregate must name
its licence, provenance, collection method, consent basis, map/game/wipe/mode/cohort scope, coverage,
gaps, transform, calibrated confidence, and model version before it can enter a signed snapshot.
