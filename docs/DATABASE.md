# Database and data cache

Tarkov Companion stores public game data in a local SQLite database. The database path is supplied through `SqliteDatabaseOptions`; the data layer creates its parent directory, enables foreign keys, uses WAL journaling, and applies a five-second busy timeout whenever it opens a connection.

The cache is local application state. It never contains game process memory, intercepted traffic, user tokens, or captured screen images. Optional TarkovTracker tokens live only behind the Windows per-user protected-storage adapter, outside SQLite.

## Startup

Run `SqliteMigrationRunner.ApplyAsync` before constructing repositories. `SqliteMigrationLedger` is the only ordered sequence: every identifier has one embedded upgrade fixture and one rollback fixture, and an unreserved filename is refused. The migration and its `schema_migrations` row commit in one transaction, so re-running the runner is idempotent.

Before any pending migration marked destructive, the runner makes a SQLite-consistent `VACUUM INTO` recovery copy beside the database and verifies it with `quick_check`. A failed or cancelled migration restores that verified copy with an atomic staged file replacement; if restoration itself fails, `SqliteMigrationException.LastRecoverableBackupPath` names the copy that still verifies. Fault injection covers backup disk-full/lock failures, interruption before commit, transactional rollback, and restoration failure. The two newest successful recovery copies are retained.

The executable does this through `IRuntimeDataStore.InitializeAsync` before it exposes database-backed state to the UI. The database is persistent under the resolved application-data root. Demo mode uses a separate application-data root and idempotently seeds a deterministic item fixture; it does not replace the repositories with in-memory fakes.

- `0001_initial.sql` creates the normalized v1 schema and FTS5 item index.
- `0002_data_cache.sql` adds normalized short-name and 24-hour price columns plus the conditional HTTP response cache.
- `0004_quest_catalog_fidelity.sql` adds mode-scoped catalog snapshots, complete objective variants, map links, zones, and orphan evidence.
- `0005_local_quest_progress.sql` adds exact profile/mode/generation progress, explicit FIR-class holdings, pins, revisions, and an append-only inverse journal.
- `0006_quest_progress_exchange.sql` adds immutable import metadata, reviewed conflict decisions, unresolved IDs, normalized-payload idempotency, and append-only undo boundaries.
- `0007` through `0010` remove superseded or unread v1 structures; their destructive status is explicit in the ledger.
- `0011_v2_data_platform.sql` introduces content-addressed raw bodies, atomic dataset publications, frozen profile/outbox stores, observed inventory and raid-field evidence, craft history, planning and model snapshots, retention/recovery state, and scheduled maintenance evidence.

Never edit an applied migration. Add a monotonically numbered migration instead.

## Source and translation flow

`TarkovDevJsonClient` reads the public GET endpoints under `https://json.tarkov.dev/{gameMode}`. It has explicit typed methods for items, maps, tasks, hideout stations, traders, crafts, barters, and the item-scoped `prices/{itemId}` history endpoint.

Translatable endpoints use two documents:

1. The base envelope supplies `data` and JSON-path entries in `translations`.
2. The language envelope at `{endpoint}_{language}` supplies translation key/value pairs under `data`.

`DataTranslationService` applies object and array wildcard paths before typed deserialization. Unknown JSON fields are retained as extension data and raw source JSON; missing unknown prices, weights, coordinates, times, and confidence remain `NULL`; and missing required identity/shape fields fail with a `JsonException`. Empty datasets, a replacement below half the held cardinality, bodies above 16 MiB, JSON deeper than 32 levels, non-finite numeric observations, mismatched dictionary identities, and malformed required fields are refused before normalized rows or cache heads change. English is covered by the checked-in synthetic fixtures; no test contacts the live service.

## HTTP cache behavior

The default freshness windows are nine hours for static datasets and ten minutes for price history. `http_response_cache` contains validators, timestamps, and a SHA-256 reference; `raw_endpoint_bodies` contains one gzip-compressed copy of each unique uncompressed UTF-8 body. Fresh responses are returned directly from that content address. A stale response is returned immediately while one deduplicated refresh runs in the background. Forced refreshes wait for revalidation, while a valid stale response remains available if the network fails.

Requests use `ETag` and `Last-Modified` validators when the server provides them. Each attempt has a 12-second default timeout, a 16 MiB decoded response ceiling, and an absolute maximum of three requests with bounded jittered exponential backoff. Only timeouts, transport failures, HTTP 408, HTTP 429, and 5xx responses are retried. Foreground cancellation propagates through response streaming; a cancelled or broken half-transfer never acquires a content address or cache key. Offline state is probed for every attempt, stale data remains visible, and a bounded background reconnect loop backs off before trying again.

The default on-disk cache is limited to 32 MiB of unique compressed bodies, 512 cache keys, and 30 days of age. `InspectAsync` reports compressed and uncompressed bytes, unique bodies, keys, access times, and quarantined documents. `CleanupAsync` has a non-mutating dry run and evicts by age then least-recent access without removing a body another key still references. A malformed or hash-mismatched local body is removed from the visible cache and recorded in `local_json_recovery`, rather than taking startup down.

## Normalized refreshes

`SqliteDataRefreshRepository` writes each endpoint snapshot in a SQLite transaction. A malformed snapshot cannot partially replace the previous endpoint data.

- Item refreshes upsert current items, remove disappeared IDs, rebuild categories, memberships, trader offers, and FTS rows, and retain historical price rows for items that still exist. `item_metrics_v2` retains nullable weight, flea, and trader measurements without converting absence to zero.
- Map, task/objective, hideout, trader, craft, and barter refreshes replace their corresponding normalized table groups atomically.
- Item price-history imports use UTC timestamps converted from Unix milliseconds and are idempotent on item, timestamp, and source. A point without a published timestamp is preserved explicitly in `price_history_unresolved_time`; it is not assigned the import time.
- Quest item/task references are source identifiers, not foreign keys to the current catalog. An upstream identifier the current item or task catalog does not resolve therefore remains an explicit row rather than disappearing during import.
- `SqliteSyncStateRepository` writes sync state, a publication attempt, and the dataset head in one transaction. `current` advances visible and last-known-good heads, `stale` advances only the visible head, and `refused` or `partial` records the attempt while retaining the previous visible/LKG identifiers. `dataset_sync_runs` distinguishes whole-run `current`, `stale`, and `partial` outcomes, including cancellation after only part of a seven-endpoint run.

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

`SqliteOutboxStore` implements the frozen runtime `IOutboxStore`: atomic batch admission, bounded active capacity, idempotency receipts, per-aggregate head ordering, fenced leases and renewals, retry/dead-letter/manual-retry/explicit-resolution transitions, restart recovery, health snapshots, and bounded completed-row retention. `SqliteRaidHistoryService` implements the target operation scope. A raid side effect and `outbox_target_operations.operation_id` commit in the same SQLite transaction, so success followed by uncertain queue acknowledgement cannot replay that effect after restart.

Raid summaries and events continue to use the existing `raids` and `raid_events` tables. `raid_field_history` keeps manual values and external observations as distinct immutable evidence with nullable observation time/confidence and explicit source. CSV and JSON export read persisted summaries; no capture bytes or screen images enter the database.

`SqliteProfileWorkspaceStore` implements the V2 profile workspace compare-and-swap contract across a workspace head, immutable identity/generation context, and normalized progress collections. `observed_inventory_snapshots` and `observed_inventory_nodes` store bounded, acyclic nested stash/container/item evidence, nullable unknown measurements, source/version/coverage/confidence, and exact forward-compatible JSON. Saving a current snapshot atomically retires the prior current snapshot in the same profile/generation/mode scope.

`craft_history` records nullable historical cost/yield/output evidence. `loadout_plans` uses revision compare-and-swap and retains extension JSON. `model_snapshots` requires source, data-through/generated times, coverage, confidence, calibration, and model version where known; the presentation kind is only historical, modelled, or predicted, and the store refuses anything labelled live. `retention_policies` keeps Debug Capture explicit and disabled by default; captures themselves are never persisted unless that separately reviewed feature is enabled.

`SqliteQuestProgressImportStore` applies one confirmed project JSON or TarkovTracker preview in a single transaction. It compares the exact base revision, records non-secret source provenance plus normalized payload and preview hashes, applies selected values, stores conflict decisions and unknown or invalid source IDs, and appends inverse journal rows before commit. Repeated payload hashes are idempotent within one exact profile scope. Undo adds a new journal batch and a separate undo boundary; it never rewrites prior history and refuses to overwrite later revisions. Stage 5 required no schema migration because the existing source-neutral import, conflict, unresolved-record, journal, and undo tables already hold this metadata.

## Verification

`SqliteDataPlatformMaintenance` exposes inspection plus dry-run/execution APIs for prune, reindex, and vacuum, and stores bounded schedules and run evidence. Prune never deletes a visible or last-known-good publication. `SqliteV2UnreadSchemaAuditor` ratchets every V2 table/column against its intentional persistence map. `SqliteQueryPlanAuditor` captures `EXPLAIN QUERY PLAN` evidence for item, quest, price, map, raid-history, profile, and craft paths.

The deterministic `DataV2` integration suite uses no Internet service. It verifies all 11 upgrade/rollback fixture pairs; destructive backup/restore and injected disk-full, lock, interruption, rollback, and restore failures; compressed raw-body deduplication and cleanup; oversize/deep/empty/half-transfer refusal; malformed local-body recovery; atomic publication/LKG semantics; nullable and unknown-JSON round trips; nested inventory; manual/observed raid provenance; model safety; profile compare-and-swap; durable ordered outbox replay fencing; maintenance dry runs; unresolved quest IDs; a 50 MiB uncompressed first-sync cache fixture; an 80,001-row catalog baseline; and indexed plans for all seven critical query families. GitHub Actions remains the required integration gate; local development-host checks are supplementary.
