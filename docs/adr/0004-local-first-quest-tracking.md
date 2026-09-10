# ADR 0004: Local-first quest tracking and source-honest map projection

Status: Accepted — 2026-09-10

## Context

Quest progress is user-authored state, while task definitions are public game-data catalog state. Treating either a third-party tracker or a freshly fetched catalog as the user's progress authority would make the application account-dependent, online-dependent, and unable to explain conflicts honestly. The current `json.tarkov.dev` task payload is also richer than the initial database schema: it contains prerequisites, failure branches, heterogeneous objectives, multiple map associations, item alternatives, and unprojected world-space zones.

Static quest geometry has a separate trust boundary. A world coordinate or map association is not enough to establish a pixel location on a community map variant, and presenting an unvalidated conversion as an exact marker would create false precision.

## Decision

Tarkov Companion owns canonical quest progress locally. Local state remains usable offline and is not rewritten by catalog refreshes. The static mode mapping is explicit: `regular` maps to `GameMode.Regular`, `pve` maps to `GameMode.Pve`, and `pvp-season` maps to `GameMode.PvpSeason`; adapters persist both values and never fall back across modes.

The production catalog is the mode-specific `json.tarkov.dev/{mode}/tasks` and `/maps` JSON API. The deprecated GraphQL API is a development-time schema reference only and is not a runtime fallback. Catalog refreshes preserve the raw source document, translated normalized records, payload hash, source URI, language, ETag, Last-Modified value, fetch time, and validation time. A replacement is published transactionally only after required identities validate. Unknown objective discriminators remain importable as explicitly unsupported records with their raw JSON.

The project-owned, versioned JSON format is the first progress interchange surface. Its v2 envelope will be implemented in Stage 4 and will carry exact mode and generation, task/objective assertions, non-secret provenance summaries, and a normalized-payload checksum. The SQLite schema is independently versioned: Stage 1 owns catalog migration `0004_quest_catalog_fidelity`; later progress, journal, exchange, and integration tables require new forward migrations and must not revise migration 0001 or this migration.

TarkovTracker may be added only after project JSON. It is optional, mode-scoped, read-only, and limited to the documented canonical-host `GET /token` and `GET /progress` operations. It never becomes required for quest, map, item-need, or recommendation behavior, and fetch time is not treated as source edit time.

Quest locations are second-screen static catalog facts. Exact points, regions, or floor placement may be displayed only when source geometry is mapped through a validated transform for the same approved map variant. Missing geometry, attribution, bounds, or transform validation produces a map-level association or an unavailable explanation, never an invented marker. This does not authorize an in-game overlay, game observation, live tracking, or gameplay input.

## Pinned source contract and attribution

The Stage 1 task contract is pinned to the regular-mode response observed on 2026-09-10:

- endpoint: `https://json.tarkov.dev/regular/tasks`;
- decoded-body SHA-256: `77bc7b164ad0d0dcc3c8097e45b6f301c1e23892819c396528a506a253ae6916`;
- ETag value observed during research: `c0348cc37ac4c137125dcef93ea5f859` (the transport may render it as a weak validator);
- research Last-Modified value: `2026-09-10T03:23:23Z`;
- task count: 515; and
- discriminator taxonomy: `buildWeapon`, `dialogue`, `experience`, `extract`, `findItem`, `findQuestItem`, `giveItem`, `giveQuestItem`, `globalVariable`, `mark`, `plantItem`, `plantQuestItem`, `sellItem`, `shoot`, `skill`, `taskStatus`, `traderLevel`, `traderStanding`, `useItem`, and `visit`.

The research-pinned regular maps body has SHA-256 `ff0459e7b7ff46a392eca19d907975c454057b9064ffccab4d37ad32e09f5d1e`. Map visual configuration remains governed by ADR 0002 and its pinned `the-hideout/tarkov-dev` revision. Synthetic contract fixtures cover the public shapes and scale without redistributing the production task/map bodies; runtime caches retain fetched payloads locally. Catalog records retain `json.tarkov.dev` source attribution and retrieval metadata. Third-party map assets retain their own author, URI, license, retrieval time, and content hash and are not bundled until their exact use is approved.

## Consequences

Catalog and progress truth remain independent. PvP, PvE, and seasonal catalogs can coexist without ID collision, repeated identical task refreshes are idempotent, and future schema variants do not make the whole catalog unavailable. The initial legacy task tables remain compatible during staged delivery, while Stage 1 queries use the mode-scoped catalog tables.

Local editing, progress journaling, project JSON exchange, TarkovTracker import, and quest UI are deliberately not implemented by Stage 1. Those capabilities require later stage-specific contracts, migrations, tests, and runtime composition. No Stage 1 catalog geometry is eligible for display as an exact marker by itself.
