# Agent A — data, cache, and persistence

Worktree: a dedicated Orca top-level worktree created from `origin/main`; use the exact current working directory reported by Orca.

Read root `AGENTS.md` before editing. Do not modify outside this worktree. Commit the finished work with a focused message.

## Ownership

- `src/TarkovCompanion.Infrastructure/TarkovDevJson/**`
- `src/TarkovCompanion.Infrastructure/Persistence/Repositories/**`
- new migrations numbered `0002_*` or higher; do not rewrite `0001_initial.sql`
- `src/TarkovCompanion.Application/Services/Data*`, `Item*`, and `Price*`
- `fixtures/api/**`
- data/persistence-specific files under `tests/TarkovCompanion.IntegrationTests/**`
- `docs/DATABASE.md`

Treat current public APIs as untrusted input. Do not inspect or copy source from reference apps.

## Deliverables

- Typed `json.tarkov.dev` HTTP client for items, maps, tasks, hideout, traders, crafts, barters, and item price history.
- Translation-envelope application layer with English fixture coverage.
- Bounded timeout/backoff, ETag/Last-Modified, request deduplication, stale-while-revalidate, and no infinite retry.
- SQLite item repository, FTS-backed search, price/history repository, sync-state updates, and atomic normalized refresh.
- Representative small legal fixtures for every endpoint family; tests never require Internet.
- Offline-cache, schema-drift, missing-optionals, migration, exact/short/fuzzy search, and stale-cache tests.

## Acceptance

- Owned projects build with zero warnings.
- New tests pass where the test host is available.
- No network-dependent test.
- No edits to UI, recognition, Windows platform, or existing Core contracts without coordinator approval.
- Commit and report commit hash, test counts, and any contract gap.
