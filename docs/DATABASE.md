# Database and data cache

Tarkov Companion stores public game data in a local SQLite database. The database path is supplied through `SqliteDatabaseOptions`; the data layer creates its parent directory, enables foreign keys, uses WAL journaling, and applies a five-second busy timeout whenever it opens a connection.

The cache is local application state. It never contains game process memory, intercepted traffic, user tokens, or captured screen images. Optional TarkovTracker tokens live only behind the Windows per-user protected-storage adapter, outside SQLite.

## Startup

Run `SqliteMigrationRunner.ApplyAsync` before constructing repositories. `SqliteMigrationLedger` is the only ordered sequence: every identifier has one embedded upgrade fixture and one rollback fixture, and an unreserved filename is refused. The migration and its `schema_migrations` row commit in one transaction, so re-running the runner is idempotent. A database that contains an unknown newer-build migration and is also missing any known migration is left intact and refused as an unsafe sidegrade; a fully current database with additive newer migration rows remains readable and reports those rows.

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

Requests use `ETag` and `Last-Modified` validators when the server provides them. Each attempt has a 12-second default timeout, a 16 MiB decoded response ceiling, and an absolute maximum of three requests with bounded jittered exponential backoff. Only timeouts, transport failures (including a reset after response headers), HTTP 408, HTTP 429, and 5xx responses are retried. Concurrent cache-miss callers share one transfer, but each waiter keeps independent cancellation; cancelling one waiter cannot cancel another caller's load. A forced request bypasses any stale background refresh. A cancelled or broken half-transfer never acquires a content address or cache key. Offline state is probed for every attempt, stale data remains visible, and one lifetime-bound background refresh per cache key backs off before trying again. Client shutdown cancels and drains those refreshes before releasing their resources.

The default on-disk cache is limited to 32 MiB of unique compressed bodies, 512 cache keys, and 30 days of age. `InspectAsync` reports compressed and uncompressed bytes, unique bodies, keys, access times, and quarantined documents. `CleanupAsync` has a non-mutating dry run and evicts by age then least-recent access. Dataset publications retain the immutable SHA-256 provenance value but do not pin disposable response bytes outside the cache budget; a body is removed once no cache key references it. A malformed or hash-mismatched local body is removed from the visible cache and recorded in `local_json_recovery`, rather than taking startup down; that quarantine delete is fenced by the exact content address observed so it cannot remove a concurrent valid replacement.

## Normalized refreshes

`SqliteDataRefreshRepository` writes each endpoint snapshot in a SQLite transaction. A malformed snapshot cannot partially replace the previous endpoint data.

- Item refreshes upsert current items, remove disappeared IDs, rebuild categories, memberships, trader offers, and FTS rows, and retain historical price rows for items that still exist. `item_metrics_v2` retains nullable weight, flea, and trader measurements without converting absence to zero.
- Map, task/objective, hideout, trader, craft, and barter refreshes replace their corresponding normalized table groups atomically.
- Item price-history imports use UTC timestamps converted from Unix milliseconds and are idempotent on item, timestamp, and source. A point without a published timestamp is preserved explicitly in `price_history_unresolved_time`; it is not assigned the import time.
- Quest item/task references are source identifiers, not foreign keys to the current catalog. An upstream identifier the current item or task catalog does not resolve therefore remains an explicit row rather than disappearing during import.
- A successful endpoint refresh writes its normalized table group, sync state, publication attempt, and dataset head in one transaction. `dataset_sync_runs.publication_order` is an SQLite `AUTOINCREMENT` sequence, not a timestamp; `BeginRunAsync` durably claims every requested endpoint before network I/O. The commit callback rejects a superseded claim inside the normalization transaction, so all replacement rows roll back with the losing head and hash. `current` advances visible and last-known-good heads, `stale` advances only the visible head, and `refused` or `partial` records the attempt while retaining the previous visible/LKG identifiers. Because normalized endpoint tables are global, `dataset_endpoint_materializations` identifies their one active mode/language context; a successful replacement nulls other contexts' visible heads and marks their scoped sync state superseded while preserving historical publications and LKG evidence. `dataset_sync_runs` distinguishes whole-run `current`, `stale`, and `partial` outcomes, including cancellation after only part of a seven-endpoint run.

`TarkovDevDataRefreshOperation` coordinates all seven static endpoint families for `DataSyncService`. One failed endpoint is reported without hiding successful independent endpoint refreshes. The item-scoped history endpoint is refreshed on demand because the upstream API does not expose a global price-history document.

## Queries

`SqliteItemRepository` implements the Core item contract. Item lookup reconstructs provenance, category membership, optional properties, and trader offers. Search combines:

- normalized full-name equality;
- normalized short-name equality;
- FTS5 token-prefix matching; and
- a bounded-result fuzzy fallback using the Application similarity function.

Exact full-name and short-name hits rank ahead of FTS and fuzzy hits. `SqlitePriceHistoryRepository`
supplies UTC chronological points to `PriceHistoryService`, which applies the requested time
window. Each resolved or unresolved item-history read admits at most 4,096 points and probes one
sentinel row; an oversized scope fails instead of silently returning a partial chart. The reader
checks SQLite's actual storage classes before touching values, accepts only canonical UTC times
and non-negative integer prices, and bounds source strings. Unresolved raw points remain visible
with their original JSON, but each document must be a bounded-depth object with bounded strings;
the read also enforces 64 KiB per document and 8 MiB across the result.

`SqliteCraftPlanningCatalog` reads a station or exact craft from the normalized `crafts`,
`craft_requirements`, and `craft_outputs` tables and attaches a caller-selected maximum of 100
history observations per craft. The component and history reads share one SQLite snapshot, so a
refresh cannot splice together two catalog generations. Cost, yield, observation time, output
count, and station level remain nullable; retained source and history JSON keep future upstream
fields available to later planners.

## Runtime and raid history

`SqliteRuntimeDataStore` reports a startup snapshot from normalized item and sync-state tables, including item count, successful endpoint count, the most recent UTC success, and the most recent bounded error summary. This is the source for the UI's data availability and freshness state.

`SqliteOutboxStore` implements the frozen runtime `IOutboxStore`: atomic batch admission, bounded active capacity, idempotency receipts, per-aggregate head ordering, fenced leases and renewals, retry/dead-letter/manual-retry/explicit-resolution transitions, restart recovery, health snapshots, and bounded completed-row retention. A permanent aggregate-sequence ledger survives completed-row pruning; `RaidHistoryOutbox` restores those cursors before its first post-restart acceptance, and the durable store refuses any spent sequence number. `SqliteRaidHistoryService` implements the target operation scope. A raid side effect and `outbox_target_operations.operation_id` commit in the same SQLite transaction, so success followed by uncertain queue acknowledgement cannot replay that effect after restart.

Raid summaries and events continue to use the existing `raids` and `raid_events` tables. `raid_field_history` keeps manual values and external observations as distinct immutable evidence with nullable observation time/confidence and explicit source. CSV and JSON export read persisted summaries; no capture bytes or screen images enter the database.

`SqliteProfileWorkspaceStore` implements the V2 profile workspace compare-and-swap contract across a workspace head, immutable identity/generation context, and normalized progress collections. Reads hold one SQLite snapshot across the head and every normalized child table, so a concurrent replacement cannot produce a mixed-revision workspace. `observed_inventory_snapshots` and `observed_inventory_nodes` store bounded, acyclic nested stash/container/item evidence, nullable unknown measurements, source/version/coverage/confidence, and exact forward-compatible JSON. Saving a current snapshot atomically retires the prior current snapshot in the same profile/generation/mode scope.

`craft_history` records nullable historical cost/yield/output evidence. Its schema rejects non-canonical history identifiers, negative or non-finite facts, empty source/time text, and SQLite dynamic-type substitutions before readers need to quarantine them. `loadout_plans` uses exact-next revision compare-and-swap and retains extension JSON. Model snapshot writes accept only the frozen typed `HistoricalIntelligence<T>` and `ModelledIntelligence<T>` evidence contracts, then verify every persisted source/time/coverage/confidence/model-version field against the canonical lineage carried by that envelope. Modelled evidence is the prediction contract; there is no live presentation path. Named calibration references stay in canonical JSON, while the legacy numeric `calibration` column remains `NULL` rather than inventing a lossy number. Queries require the exact nullable profile, generation, and game-mode context. `retention_policies` keeps Debug Capture explicit and disabled by default; captures themselves are never persisted unless that separately reviewed feature is enabled. Local user history has no implicit retention period: a missing policy or a `NULL` `data_retention_days` value preserves it.

`SqliteQuestProgressImportStore` applies one confirmed project JSON or TarkovTracker preview in a single transaction. It compares the exact base revision, records non-secret source provenance plus normalized payload and preview hashes, applies selected values, stores conflict decisions and unknown or invalid source IDs, and appends inverse journal rows before commit. Repeated payload hashes are idempotent within one exact profile scope. Undo adds a new journal batch and a separate undo boundary; it never rewrites prior history and refuses to overwrite later revisions. Stage 5 required no schema migration because the existing source-neutral import, conflict, unresolved-record, journal, and undo tables already hold this metadata.

## Verification

`SqliteDataPlatformMaintenance` exposes inspection plus dry-run/execution APIs for prune, reindex, and vacuum, stores at most 256 run records, and makes both schedules and run diagnostics queryable. The migrated prune schedule is disabled and not due. A scheduled prune runs only after the `local` policy has an explicit non-`NULL` integer `data_retention_days`; the supported range is 1 through 365,000 days, and startup derives the cutoff from that value instead of supplying a hidden default. The schema and due-run reader reject SQLite dynamic-type substitutions as well as out-of-range values. After claiming a due schedule, prune revalidates the exact claim and retention value while acquiring the delete transaction's writer lock, so a concurrent settings change that wins the race revokes the old authorization and preserves history. Prune never deletes a visible or last-known-good publication; superseded publication links are detached transactionally before expired history is removed. Raw response bytes remain governed solely by the cache budget while publication hashes survive eviction as provenance. `SqliteV2UnreadSchemaAuditor` ties each read claim to a compiled production type and method, rejects a declaration when that reader no longer exists, and does not count writes. The measured `0011` result reports six fields honestly: `item_metrics_v2.measured_utc`, `item_metrics_v2.source`, `profile_workspaces.updated_utc`, and `outbox_target_operations.command_kind`, `target_id`, and `applied_utc` have no product read yet. Fixture-added columns, missing tables, stale schema declarations, and missing-reader declarations are separate ratchet failures rather than being hidden by a nominal zero.

`SqliteQueryPlanAuditor` runs `EXPLAIN QUERY PLAN` against SQL constants used by the real item, quest, price, map, raid-history, profile, and craft readers. It therefore records the map catalog, raid-history list, and profile-context catalog as intentional whole-table reads instead of substituting indexed synthetic lookups those repositories never execute. Point and scope queries still require indexed plans, including map extracts, map trails, the profile workspace head, and all four station-craft reads. Separate exact-shape assertions cover publication run/state summaries and the bounded newest-first raid-field window.

The deterministic integration suite uses no Internet service. Its `DataV2` coverage executes every one of the 11 rollback fixtures against its populated upgraded schema; verifies destructive backup/restore and injected disk-full, lock, interruption, rollback, restore, and publication failures; exercises compressed raw-body deduplication and cleanup; rejects oversize/deep/empty/half-transfer input; and preserves explicit recovery for malformed profile, settings, and cache JSON. It also proves atomic normalized-row/publication/LKG semantics; timeout, connection-reset, and 5xx outcomes through the whole sync; nullable and unknown-JSON round trips; bounded typed nested inventory; manual/observed raid provenance; typed model safety; exact-next profile compare-and-swap; durable ordered outbox replay fencing; and scheduled maintenance with non-mutating dry runs. The production quest-exchange integration coverage proves unresolved catalog IDs persist explicitly. Measured gates cover a representative seven-endpoint first-sync footprint below 50 MiB, production-client parsing of 80,001 item records and a 2.3 MiB map document, an 80,001-row exact-lookup baseline, and the real access plans for all seven critical query families. GitHub Actions is the required build and integration gate; no .NET workload is run on the workstation.
