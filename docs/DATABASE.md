# Database and data cache

Tarkov Companion stores public game data in a local SQLite database. The database path is supplied through `SqliteDatabaseOptions`; the data layer creates its parent directory, enables foreign keys, uses WAL journaling, and applies a five-second busy timeout whenever it opens a connection.

The cache is local application state. It never contains game process memory, intercepted traffic, user tokens, or captured screen images.

## Startup

Run `SqliteMigrationRunner.ApplyAsync` before constructing repositories. Migrations are embedded resources, sorted by filename, and recorded in `schema_migrations` within the same transaction as their SQL. Re-running the migration runner is idempotent.

The executable does this through `IRuntimeDataStore.InitializeAsync` before it exposes database-backed state to the UI. The database is persistent under the resolved application-data root. Demo mode uses a separate application-data root and idempotently seeds a deterministic item fixture; it does not replace the repositories with in-memory fakes.

- `0001_initial.sql` creates the normalized v1 schema and FTS5 item index.
- `0002_data_cache.sql` adds normalized short-name and 24-hour price columns plus the conditional HTTP response cache.

Never edit an applied migration. Add a monotonically numbered migration instead.

## Source and translation flow

`TarkovDevJsonClient` reads the public GET endpoints under `https://json.tarkov.dev/{gameMode}`. It has explicit typed methods for items, maps, tasks, hideout stations, traders, crafts, barters, and the item-scoped `prices/{itemId}` history endpoint.

Translatable endpoints use two documents:

1. The base envelope supplies `data` and JSON-path entries in `translations`.
2. The language envelope at `{endpoint}_{language}` supplies translation key/value pairs under `data`.

`DataTranslationService` applies object and array wildcard paths before typed deserialization. Unknown JSON fields are retained as extension data, missing optional fields receive conservative defaults, and missing required fields fail with a `JsonException`. English is covered by the checked-in synthetic fixtures; no test contacts the live service.

## HTTP cache behavior

The default freshness windows are nine hours for static datasets and ten minutes for price history. Fresh responses are returned directly from `http_response_cache`. A stale response is returned immediately while one deduplicated refresh runs in the background. Forced refreshes wait for revalidation, while a valid stale response remains available if the network fails.

Requests use `ETag` and `Last-Modified` validators when the server provides them. Each attempt has a hard timeout. Only timeouts, transport failures, HTTP 408, HTTP 429, and 5xx responses are retried, with jittered exponential backoff and an absolute maximum of three attempts. Cancellation propagates to foreground I/O.

## Normalized refreshes

`SqliteDataRefreshRepository` writes each endpoint snapshot in a SQLite transaction. A malformed snapshot cannot partially replace the previous endpoint data.

- Item refreshes upsert current items, remove disappeared IDs, rebuild categories, memberships, trader offers, and FTS rows, and retain historical price rows for items that still exist.
- Map, task/objective, hideout, trader, craft, and barter refreshes replace their corresponding normalized table groups atomically.
- Item price-history imports use UTC timestamps converted from Unix milliseconds and are idempotent on item, timestamp, and source.
- `SqliteSyncStateRepository` records attempt/success UTC timestamps, cache validators, content hashes, stale/current/failed status, and a bounded error summary per source, mode, and language.

`TarkovDevDataRefreshOperation` coordinates all seven static endpoint families for `DataSyncService`. One failed endpoint is reported without hiding successful independent endpoint refreshes. The item-scoped history endpoint is refreshed on demand because the upstream API does not expose a global price-history document.

## Queries

`SqliteItemRepository` implements the Core item contract. Item lookup reconstructs provenance, category membership, optional properties, and trader offers. Search combines:

- normalized full-name equality;
- normalized short-name equality;
- FTS5 token-prefix matching; and
- a bounded-result fuzzy fallback using the Application similarity function.

Exact full-name and short-name hits rank ahead of FTS and fuzzy hits. `SqlitePriceHistoryRepository` supplies UTC chronological points to `PriceHistoryService`, which applies the requested time window.

## Runtime and raid history

`SqliteRuntimeDataStore` reports a startup snapshot from normalized item and sync-state tables, including item count, successful endpoint count, the most recent UTC success, and the most recent bounded error summary. This is the source for the UI's data availability and freshness state.

`SqliteRaidHistoryService` implements `IRaidHistoryService` over the existing `raids` and `raid_events` tables. It creates summary rows on evidence-based raid starts, records state/position/extract/scan events with UTC timestamps and validated JSON, and closes the summary row on a transition out of `InRaid`. CSV and JSON export read those persisted summaries; no capture bytes or screen images enter the database.

## Verification

The integration suite copies only the small synthetic files under `fixtures/api`. It verifies migrations, all endpoint shapes, translation, schema drift, missing optionals, retry limits, conditional requests, request deduplication, stale-while-revalidate/offline fallback, transactional refresh, exact/short/fuzzy search, price history, persistent runtime startup, offline restart, raid transitions, and history export without requiring Internet access.
