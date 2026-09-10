# Quest Tracking Architecture

This document is a research-backed proposal for quest and objective tracking in
Tarkov Companion. It describes a safe v1 architecture; it does not assert that
the feature is already implemented. The research snapshot is 2026-09-10 and
uses source revisions pinned in the Sources section where possible.

## Executive decision

Build an owned, local-first quest tracker and make it the canonical store for
the user's progress. Add imports behind narrow adapters: the project's own
versioned JSON format first, then an optional read-only TarkovTracker progress
adapter. Every external import is mode-scoped, previewable, provenance-bearing,
and reversible as a batch. Tarkov Companion must not write to TarkovTracker or
any other external progress service in v1.

Use `json.tarkov.dev/{gameMode}/tasks` and `/maps` as the runtime catalog. Use
the tarkov-api GraphQL schema only as a development-time schema reference, not
as a production fallback: current upstream guidance says that the old GraphQL
API is deprecated/unstable for new external integrations and directs consumers
to `json.tarkov.dev`.[^1] Preserve the raw source documents and HTTP validators
alongside normalized records so new objective variants can be added without
losing data.

This decision keeps core operation offline-capable, avoids dependence on a
third-party account, and remains compatible with a user who already tracks
progress elsewhere. It also respects the permanent safety boundary: the app
does not inspect Escape from Tarkov memory or network traffic, hook its
renderer, inject code, generate gameplay input, or automate any game action.

## Scope and non-goals

The v1 tracker should answer four user questions:

1. Which quests have I recorded as active, completed, or failed?
2. Which incomplete objectives and hand-in items matter now?
3. Which recorded active objectives relate to the map I am viewing?
4. Where did each catalog fact and progress assertion come from, and how old is
   it?

The v1 tracker is informational and second-screen only. It does not:

- infer progress by reading the game process, renderer, packets, or private
  protocol;
- generate mouse or keyboard input, accept or turn in tasks, move inventory,
  or otherwise operate the game;
- present static quest geometry as a live player or enemy detection;
- claim an objective is complete unless the user or an explicitly identified
  import source said so;
- require TarkovTracker, a Battlestate account, or any external account;
- use the deprecated GraphQL service at runtime;
- copy GPL implementation code or third-party map artwork into the MIT project.

Screen recognition may later help a user search for a quest or item, but it is
not a progress authority in this design. Any future log-file import needs a
separate clean-room format review and an explicit user-selected file; it is not
part of this v1 decision.

## Evidence and source assessment

### Runtime catalog: `json.tarkov.dev`

The published endpoint catalog currently exposes mode-specific task and map
documents for `regular`, `pve`, and `pvp-season`, plus translations and other
structured datasets.[^2] On 2026-09-10, the regular task document was about
2.1 MB and contained 515 tasks; a separate English translation document was
applied to its translation keys. Those counts are observations, not contract
constants. The response supplied `ETag` and `Last-Modified` validators, which
are useful for conditional refresh; no task-specific service level or immutable
version contract was found.

For reproducibility, the response observed at 2026-09-10 05:14 UTC had ETag
`"c0348cc37ac4c137125dcef93ea5f859"`, Last-Modified `2026-09-10
03:23:23 UTC`, and decoded-body SHA-256
`77bc7b164ad0d0dcc3c8097e45b6f301c1e23892819c396528a506a253ae6916`.
The regular maps body observed in the same research pass had SHA-256
`ff0459e7b7ff46a392eca19d907975c454057b9064ffccab4d37ad32e09f5d1e`.
No `Cache-Control` header was observed on the task response, so local freshness
must be an explicit application policy rather than inferred from HTTP cache
metadata.

The current task payload is substantially richer than the repository's
normalized task records. It includes:

- prerequisites with required task states, including `complete` and `active`;
- trader, player-level, faction, Kappa, Lightkeeper, prestige, delay, failure,
  and restartability metadata;
- heterogeneous objective types, not just item hand-in objectives;
- task and objective map associations, with some objectives linked to multiple
  maps;
- zones containing world positions, outlines, and vertical bounds;
- required keys, acceptable item sets, quantities, and found-in-raid flags;
- start, finish, and failure outcomes; and
- translation-key documents layered with language maps.

The same snapshot contained twenty distinct objective type strings. Common
examples included `giveItem`, `visit`, `shoot`, `findItem`, `plantItem`,
`findQuestItem`, `giveQuestItem`, `extract`, and `mark`; less common variants
included `taskStatus`, `useItem`, `traderStanding`, and `globalVariable`.[^3]
The parser must therefore use a discriminated objective model plus an
`Unsupported` variant that retains raw JSON. A closed switch that rejects the
whole dataset when a new objective type appears would make schema drift an
availability incident.

The maps document currently includes stable map identities, names, normalized
names, durations, extracts, spawns, and other world-coordinate entities.[^4]
It does not, by itself, define every pixel projection needed by a companion
map. Current tarkov.dev map configuration carries projection, bounds,
transforms, rotation, floor/layer, attribution, and SVG path data.[^5] Quest
zones may only be drawn when an approved map asset and a validated transform
for the same map variant are both present. Otherwise the UI shows a map-level
association without inventing a point.

Refresh policy should follow the repository's existing static-data policy:
nine-hour stale-while-revalidate, explicit UTC `fetchedAt`, conditional GET,
bounded retries, and usable stale data on failure. The UI should show the
catalog age. A user-requested refresh may bypass freshness, but it still uses
validators and never deletes a valid local catalog before a replacement has
parsed and committed transactionally.

### GraphQL: schema oracle, not runtime dependency

The open tarkov-api schema is valuable because it documents the `Task`
surface and the objective interface and subtype fields in a readable form.[^6]
Its examples also demonstrate that clients must query objective inline
fragments rather than assume one universal shape.[^7] It is appropriate for
fixture design, field discovery, and review of static JSON mapping.

It is not appropriate as the v1 runtime source. The current TarkovTracker
system documentation—maintained by a major consumer in the same ecosystem—says
the old GraphQL endpoint is deprecated, unstable, and should not be used by new
external tooling.[^1] Runtime code should have one task-catalog provider in v1:
the static JSON endpoint required by this project's permanent rules. A GraphQL
fallback would double schema, caching, failure, and attribution paths while
depending on an endpoint upstream asks new consumers to avoid.

### Quest progress semantics

Quest progress is not a Boolean on a task. The source catalog and current Wiki
documentation support at least these distinctions:

- prerequisite eligibility is different from acceptance;
- task acceptance/activity is different from objective progress;
- all required objectives being recorded complete is different from the task
  being turned in and recorded complete;
- finding an item and handing it over may be distinct objectives;
- some hand-ins require found-in-raid items while others do not;[^8]
- an objective can accept one of several item identities;
- objectives can be optional, scoped to multiple maps, or not mappable;
- a task can fail because of a branch or condition, and some tasks can restart;
- some progress can reset when an extraction or failure condition is not met;
  and
- PvP, PvE, and seasonal profiles are independent progress universes.

The live structured catalog is the machine-readable authority for these
fields. The Escape from Tarkov Wiki is supporting user-facing evidence: its
quest overview distinguishes task objective categories,[^20] and individual
quest documentation illustrates that some recorded objective progress can be
reset when the task's extraction conditions are not met.[^21]

For example, a quest can separately require visiting a location and handing
over three found-in-raid medical items.[^9] That means one `progress_count`
cannot stand for the state of the whole task, and an item card must distinguish
"required by an incomplete objective" from "currently held." The app never
knows the latter unless the user explicitly records it.

The tracker should model what was recorded, not pretend to observe the game.
Derived labels should use wording such as "recorded objectives satisfied" and
retain their inputs. `Unknown` is a first-class state; it must not silently
become `false`, `0`, or `not started`.

### TarkovTracker interoperability

TarkovTracker is a credible optional interoperability source because its
current repository documents local browser tracking without an account,
account-backed synchronization, game-mode separation, map/objective views,
and bearer-token API access.[^10]

Its supported public progress API is `https://api.tarkovtracker.org`. Stage 5
uses only the canonical `/token` and `/progress` paths. The documented legacy
cross-host route may redirect, and an Authorization header can be lost during
that redirect; the client disables automatic redirects and rejects every
redirect instead of forwarding credentials.[^11] Internal routes are not part
of this adapter.

The current OpenAPI contract defines:

- bearer tokens tied to one exact game mode, with current prefixes for PvP,
  PvE, and seasonal profiles;
- `GET /token` and `GET /progress` under the progress-read permission;
- task progress containing task ID, complete, optional failed, and invalid;
- objective progress containing objective ID, complete, optional count, and
  invalid;
- a response envelope containing exact game mode and profile metadata;
- optional team-read access under a separate permission; and
- write endpoints for task, objective, level, and batch mutation under a write
  permission.[^12]

V1 uses only token validation and the user's own `GET /progress`. It does not
request team access, does not call write endpoints, and does not silently
upgrade scopes. The adapter must fail closed if the returned mode does not
match the target local profile.

The documented free tier currently allows 1,000 reads per day. Conditional
requests use weak ETags and private 15-second caching, but a `304` still counts
against quota; the docs recommend polling no faster than once per minute.[^11]
A companion does not need minute-level progress. Stage 5 performs no startup or
background polling: Connect performs token validation, manual Refresh fetches
progress, and the foreground refresh entry point rejects calls less than 60
seconds apart. It honors `Retry-After`, pauses when reported quota is exhausted,
and surfaces quota state. This remains a snapshot import, not a live feed.

The public progress response has no per-task or per-objective source-event
timestamp.[^12] Consequently, automatic last-write-wins reconciliation would
be fabricated. `observedAt` can say when Tarkov Companion fetched a snapshot,
but not when the user changed a field in TarkovTracker.

TarkovTracker also has a versioned application backup implementation. The
current source identifies a `tarkovtracker-backup` format and exports multiple
mode profiles, but this is app implementation rather than a documented stable
third-party interchange contract.[^13] A future importer may accept explicitly
supported versions through a clean-room adapter, preview every effect, and
quarantine unknown fields/IDs. It must not copy the GPL implementation.

Public player snapshots from tarkov.dev are another possible one-shot source,
but TarkovTracker's documentation notes that such profile data is refreshed
after a human views the profile and must be handled with exact mode/season
identity.[^14] It is unsuitable for background truth or freshness-sensitive
sync. It can be considered later as an explicitly stale, user-initiated import.

### Licensing and attribution

The service/data boundary is preferable to incorporating upstream code:

- `the-hideout/tarkov-api` is GPL-3.0.[^15]
- `tarkovtracker-org/TarkovTracker` is GPL-3.0.[^16]
- the tarkov.dev website/configuration repository is MIT.[^17]
- the SVG map repository uses CC BY-NC-SA 4.0 plus an additional anti-cheat
  restriction described by its maintainers.[^18]

This project may independently implement clients against documented HTTP
contracts without copying GPL source. Source review in this document is used
to understand observable contracts and interoperability, not as implementation
material. Preserve endpoint, record, asset, author, license, source URL, and
retrieval time in provenance. Do not bundle or modify restrictive map assets
until the existing asset-license review has approved that exact use. The API
site says the API is freely available for tools and services, but a separate
license for wholesale payload redistribution was not located; attribution and
cache-only redistribution are the conservative defaults.[^19] This is an
engineering policy, not legal advice.

## Current repository gap

The repository has useful foundations but not a production quest tracker:

- the initial migration stores task/objective/item rows and profile task and
  objective progress;
- profile JSON already carries completed task IDs and objective counts;
- item-need aggregation can subtract recorded objective progress; and
- the JSON client already provides validators, bounded retry, deduplication,
  and stale fallback.

The normalized task model currently loses material semantics: multiple maps,
prerequisite status, failure/restart state, optionality, many objective subtype
fields, and zone structure. It stores only the first objective map and relies
on raw JSON for the rest. The profile model expresses completion and counts,
but cannot faithfully represent active, failed, unknown, source provenance,
mode generation, or a reconciliation conflict. The existing architecture
review also found the profile/quest services are not composed into the running
application.

Implementation should add a forward migration and new narrow contracts. Do not
rewrite migration `0001`, do not turn raw JSON into an unqueryable substitute
for normalization, and do not bind Core to SQLite, HTTP, Avalonia, Windows, or
filesystem-watcher types.

## Options considered

### Option A: TarkovTracker is canonical

The app could require a TarkovTracker token and render its progress snapshot.
This reduces initial local editor work and can follow an existing user's data.
It is rejected for v1 because it makes core operation depend on an account,
network availability, quota, a third-party contract, and a mode-specific
secret. It also cannot perform reliable last-write-wins reconciliation because
the public response lacks per-field event times. Using its mutation endpoints
would violate the v1 no-external-write decision.

### Option B: local-only owned tracker

The app could ignore all external progress and provide only manual editing plus
its own JSON backup. This is the smallest trustworthy domain and has the best
offline/privacy properties. It is viable as the first implementation stage,
but it creates unnecessary re-entry cost for users who already maintain
progress elsewhere.

### Option C: local canonical state with optional imports

The app owns progress and works offline, while external sources submit
mode-scoped snapshots to a common preview/reconciliation boundary. Users can
import once or refresh read-only without surrendering their local record. The
extra work is an import journal, field provenance, and conflicts, but that work
is necessary for honest interoperability in any case.

Option C is recommended. It deliberately starts as Option B, then adds the
project JSON adapter and TarkovTracker adapter in later stages. No integration
is allowed to become a hidden prerequisite for quest, map, item-need, or
recommendation features.

## Recommended architecture

```text
 json.tarkov.dev tasks/maps     approved map configuration/assets
              |                              |
              +-------- catalog ingest -----+
                              |
                       catalog snapshots
                              |
                              v
 manual edits ----------> application commands
 project JSON --\                  |
 TT GET /progress ----> import preview/reconcile
 future adapters --/               |
                              local progress
                         + change/import journal
                              |
                +-------------+-------------+
                |             |             |
          quest read model  map projection  item needs
                |             |             |
                +------- second-screen UI --+
```

There are two independent truth domains:

1. **Catalog truth** describes what tasks and objectives exist in a particular
   source snapshot. It comes from public structured data and is replaced only
   by a validated transactional refresh.
2. **Profile truth** describes what a user recorded or imported for one local
   profile. It remains local and mutable. Catalog refresh never silently
   rewrites profile assertions.

The application joins these domains into read models. A missing catalog ID
does not delete progress; it creates an orphan/quarantine record visible in
diagnostics until a later catalog resolves it.

### Domain model

Use stable source IDs as external identities and internal surrogate keys only
where SQLite needs them. All mutable rows use UTC timestamps.

#### Catalog snapshot

- `CatalogSnapshot`: source, game-data mode, schema/version hint if available,
  payload hash, ETag, Last-Modified, fetched UTC, validated UTC, language, and
  raw-document cache reference.
- `TaskDefinition`: task ID, localized name/description, trader, minimum level,
  faction, restartable, Kappa/Lightkeeper/prestige flags, delay information,
  primary map if supplied, and raw source JSON.
- `TaskRequirement`: prerequisite task ID and the required status set. An
  unknown status is retained, not coerced to completion.
- `TaskFailureCondition`: source kind and target/status information needed to
  explain mutually exclusive or failed branches.
- `ObjectiveDefinition`: objective ID, task ID, discriminated kind, localized
  description, target count, optional flag, found-in-raid requirement, subtype
  payload, and raw source JSON.
- `ObjectiveItemTarget`: objective ID, item ID, target role, acceptable-set
  group, quantity if source-specific, and found-in-raid rule.
- `ObjectiveMapLink`: objective ID plus map ID. This is many-to-many.
- `ObjectiveZone`: objective ID, zone source ID, map ID, world position,
  outline, bottom/top elevation, required keys, and raw geometry. A zone is not
  a display point until a separate projection succeeds.

Rewards and messages may remain in raw JSON until a UI requires them, but the
normalizer must not discard them. Unknown objective kinds produce a valid
`UnsupportedObjectiveDefinition` and a diagnostic, not a failed full refresh.

#### Profile identity

- `LocalProfile`: opaque local ID, display name, game mode, faction, edition,
  level if known, profile generation, created UTC, and modified UTC.
- `GameMode`: an explicit enum with `Pvp`, `Pve`, and `PvpSeason`; no default
  cross-mode fallback.
- `ProfileGeneration`: an opaque local boundary for wipe/reset history. A PvP
  season identifier may be recorded when the source supplies one. Never infer
  that PvE and PvP share a generation.

Map the static catalog mode explicitly: `regular` to local `Pvp`, `pve` to
`Pve`, and `pvp-season` to `PvpSeason`. Validate this mapping at the adapter
boundary and persist both the local enum and original source value.

#### Recorded progress

- `ProfileTaskState`: profile ID, task ID, recorded state (`Unknown`,
  `NotStarted`, `Active`, `Completed`, or `Failed`), source assertion,
  effective local revision, and modified UTC.
- `ProfileObjectiveState`: profile ID, objective ID, state (`Unknown`,
  `InProgress`, or `Completed`), optional nonnegative count, source assertion,
  local revision, and modified UTC.
- `ProfileItemHolding`: optional, explicit user-entered count by item and
  found-in-raid class. Absence means unknown, not zero. This is not inferred
  from screenshots, game memory, or traffic.
- `QuestPin`: profile ID, task/objective ID, pin state, sort order, and note.
- `ProgressChange`: append-only audit entry containing entity/field, previous
  and new normalized values, actor (`User`, `Import`, or `SystemMigration`),
  source, correlation/import ID, and recorded UTC.
- `ImportSession`: adapter, external identity fingerprint, exact mode,
  source-observed UTC, fetch/import UTC, ETag/revision if supplied, payload
  hash, result, and rollback boundary.
- `ImportConflict`: import ID, entity/field, local value, incoming value,
  reason, suggested resolution, and final user decision.
- `UnresolvedExternalRecord`: import ID, external ID, type, sanitized raw
  fragment, and reason such as missing catalog ID or unsupported field.

An external token, raw Authorization header, or recoverable token fragment is
not a profile field and never appears in `ProgressChange`, exports, logs, crash
reports, or diagnostics.

#### Derived state

Derived state is recomputable and should not be accepted as imported truth:

- eligibility: `Locked`, `Available`, `Delayed`, or `Indeterminate`;
- objective summary: incomplete, recorded complete, optional, and unsupported;
- task summary: recorded active/complete/failed plus "recorded required
  objectives satisfied";
- remaining item requirement: target quantity minus recorded objective count,
  clamped at zero; and
- map relevance: active/pinned objectives joined to map and projected zones.

Eligibility evaluation needs player level, faction, trader state, time delay,
and prerequisite statuses. If any required input is unknown, the result is
`Indeterminate` with reasons. Cycles or missing prerequisite tasks are catalog
diagnostics, not recursion crashes.

The app must not auto-promote a task to `Completed` just because all known
objectives are complete. Turn-in is a separate user/import assertion. Likewise,
marking a task complete may visually satisfy its objectives without overwriting
their individual audit history.

### Application boundaries

Keep Core records platform-independent. Define small interfaces around user
intent and read models rather than a generic repository:

- `IQuestCatalog`: retrieve versioned tasks/objectives, requirements, item
  targets, and map links for one data mode.
- `IQuestProgressStore`: load and transactionally change task/objective state
  for one local profile.
- `IQuestProgressJournal`: append changes, imports, conflicts, and rollback
  markers.
- `IQuestProgressImportSource`: validate an external identity and produce a
  normalized, mode-scoped `ProgressSnapshot`; it cannot apply changes.
- `IQuestImportPlanner`: diff a snapshot against local state and return an
  explicit preview with safe, conflicting, ignored, and unresolved changes.
- `IQuestImportApplier`: transactionally apply a confirmed preview and journal
  its inverse.
- `IQuestReadService`: produce quest list/detail and item-need read models.
- `IQuestMapProjectionService`: convert supported static objective geometry to
  an approved map variant and report why projection is unavailable.
- `IProfileExchange`: import/export only the project's versioned local format.
- `IIntegrationSecretStore`: save/load/delete secrets behind a Windows
  implementation; Core and Application see opaque handles, not DPAPI.

The progress store and journal participate in one application transaction
boundary and cannot commit independently. This keeps the state mutation,
change record, and import rollback information atomic without exposing a
SQLite transaction type above Infrastructure.

Representative application commands are `SetTaskState`,
`SetObjectiveProgress`, `SetItemHolding`, `PinQuest`, `PreviewProgressImport`,
`ApplyProgressImport`, `UndoProgressImport`, and `DisconnectProgressSource`.
Representative queries are `GetQuestBoard`, `GetQuestDetail`,
`GetActiveMapObjectives`, `GetQuestItemNeeds`, and `GetSyncStatus`.

Infrastructure owns:

- static tarkov.dev HTTP DTOs and normalization;
- SQLite repositories and forward migrations;
- atomic JSON file exchange;
- the supported TarkovTracker HTTP adapter;
- HTTP handler configuration; and
- asset/config cache provenance.

Platform.Windows owns the protected-storage implementation. Any DPAPI or other
Windows-native calls remain there behind `IIntegrationSecretStore`.

Avalonia ViewModels consume commands and read models. They do not parse source
JSON, evaluate prerequisite graphs, merge imports, store tokens, or perform
coordinate transforms.

### Local edit invariants

Every progress command validates the profile/mode and catalog identity before
committing. The minimum invariants are:

- counts are finite and nonnegative;
- a known target count is a display cap, but retain an explicitly imported
  over-target value in diagnostics rather than corrupting it silently;
- `Completed` task versus `Failed` task is a deliberate replacement recorded
  in the journal;
- objective completion with no count is valid for Boolean objectives;
- count progress does not imply possession of items;
- a found-in-raid requirement is attached to the objective target, not to the
  global item identity;
- catalog mode and profile mode must match; and
- one command and its journal entry commit in the same transaction.

### Import and conflict policy

All adapters return the same normalized `ProgressSnapshot` and never write the
database directly. The preview displays source, exact mode, fetch time,
available source revision, target profile, new records, promotions,
regressions, conflicts, and unknown IDs. Apply uses the preview hash and local
base revision; if the profile changed after preview, re-plan instead of applying
a stale diff.

Safe automatic proposals are deliberately monotonic:

- incoming `complete=true` may promote an unknown/not-started/active task when
  local state is not failed;
- incoming objective completion may promote an incomplete objective;
- an increased count may be proposed when no explicit local reset or
  contradictory task state occurred after the prior import;
- a source record absent from a snapshot means unknown/no assertion and never
  deletes local progress; and
- repeated application of the same payload hash is idempotent.

Require explicit conflict resolution for:

- completed versus failed;
- any incoming regression (`complete=false`, lower count, or uncompleted task)
  against stronger local progress;
- count changes around a failed or restartable task;
- cross-mode, cross-generation, or ambiguous seasonal data;
- one external ID resolving to multiple catalog entities; and
- imports from an older observed snapshot when the adapter can prove ordering.

Because TarkovTracker does not expose per-field event timestamps, imported
snapshot time is not used as a last-write-wins clock. Local explicit edits win
by default over regressions. Users may choose "use incoming" for an individual
conflict or a reviewed batch. Store the rejected value and reason so the same
unchanged snapshot does not repeatedly nag the user.

Automatic refresh may auto-apply only non-conflicting monotonic promotions when
the user has enabled that policy. The default first import is preview-only.
Undo restores the pre-import local values as a new journaled change; it does
not rewrite history or contact the source.

### TarkovTracker adapter security

The adapter is optional and has an explicit connect flow. The disconnected
feature is available by default on Windows when protected storage exists; an
environment setting provides an explicit opt-out, while offline mode and
unavailable protected storage disable network actions. Merely composing the
runtime or reading status performs no network request. It must:

1. accept a user-generated token through a password-masked field;
2. store it through `IIntegrationSecretStore` using Windows protected storage;
3. send it only to the exact allowlisted HTTPS origin
   `https://api.tarkovtracker.org`;
4. disable automatic redirects and reject a redirect rather than forward an
   Authorization header;
5. send the documented, descriptive `User-Agent`;
6. validate token permissions and exact returned game mode;
7. expose only `GET /token` and `GET /progress` in its interface;
8. use ETag, bounded timeout, cancellation, backoff, quota headers, and
   `Retry-After`;
9. redact tokens and authorization headers at the first logging boundary; and
10. delete the secret and stop polling immediately on disconnect.

Do not model write methods and promise not to call them; omit them from the v1
adapter altogether. Do not use team progress. Never put the token in a URL,
profile export, telemetry event, exception text, or fixture.

### Local JSON exchange

The project's interchange format is the only v1 format Tarkov Companion owns.
Version it independently of the SQLite schema. A suggested version 2 envelope
contains:

- format identifier and format version;
- exporter application version and exported UTC;
- one or more profiles with exact mode and profile generation;
- task states, objective states/counts, pins, and optional explicit holdings;
- source IDs and optional non-secret provenance summaries; and
- a checksum over the normalized payload.

Import parses into memory, applies hard size/depth/count limits, rejects missing
required fields and non-finite/negative counts, tolerates unknown fields, and
shows a preview before a transaction. Export writes a temporary file, flushes,
and atomically replaces the destination. Secrets, absolute local paths, raw
authorization data, and external account identifiers are excluded by default.

TarkovTracker backup support, if added, gets its own named version adapters and
never masquerades as the project format. Unsupported versions produce a useful
message and no partial database mutation.

## Map-linked quest and item UX

### Quest board

The primary quest view should provide local, source-honest filters:

- recorded Active, Available, Locked, Completed, Failed, and Indeterminate;
- trader, map, objective kind, and found-in-raid item requirement;
- pinned and "recorded objectives satisfied"; and
- profile/game mode.

Each row shows recorded status separately from derived eligibility/readiness.
A source badge explains `Manual`, `Project import`, or `TarkovTracker import`,
with observed/fetched age. The global banner separately shows catalog age and
progress-import age so fresh task definitions never imply fresh player
progress.

Quest detail shows the prerequisite graph and unknown inputs, task/failure
branch notes, objective checklist/count controls, acceptable items, found-in-
raid requirements, map associations, source attribution, and an audit drawer.
Unsupported objective variants remain visible with their source description
and an "unsupported details" label.

### Map view

The map quest layer is opt-in and off-game-window. It shows only static catalog
relationships for the selected local profile:

- active incomplete objectives by default;
- optional toggles for available, pinned, completed, and unsupported
  objectives;
- objective type, quest, trader, floor/elevation hint, required key, and item
  chips;
- provenance and catalog timestamp on details; and
- a clear "map association only—exact location unavailable" presentation when
  no validated zone projection exists.

For zones with validated data, render a region/polygon or source-authored point
rather than a fabricated center. Respect floor/layer filters and distinguish
overlapping vertical zones. Transform failures, map-ID mismatches, out-of-bounds
coordinates, and missing attribution suppress geometry and produce a
diagnostic. The layer never renders the player, enemies, loot presence, a live
route, or inferred game state.

### Item views

An item can participate in several semantically different relationships. Show
them separately:

- **Needed for recorded active objectives:** remaining hand-in/find amount,
  found-in-raid rule, and linked quests.
- **Needed for available/future quests:** planning quantity, clearly not an
  active need.
- **Explicitly recorded held:** user-entered FIR/non-FIR count, or Unknown.
- **Acceptable alternative:** one member of an objective's acceptable item set,
  not an instruction to collect every member.

Do not subtract non-FIR holdings from a FIR requirement. Do not call an item
"owned," "found," or "handed in" based on OCR, catalog data, or a price scan.
Item recommendations consume this read model and preserve the profile,
catalog snapshot, source, and timestamp that produced it.

## Privacy and failure behavior

Progress remains in the local SQLite database and the user's explicit export
files. The network client fetches public catalog data and, only after opt-in,
the authenticated user's TarkovTracker snapshot. No progress is uploaded.

The connection screen explains the host, read-only calls, polling schedule,
stored-secret mechanism, and disconnect/delete behavior before accepting a
token. Logs identify status code, endpoint name, request correlation, quota,
and elapsed time, but not query payloads containing identity, display name,
tokens, or response bodies. Diagnostic bundles use a stable random integration
ID rather than an account identity and require the user to opt in to any
sanitized progress sample.

Failure behavior is local-first:

- catalog refresh failure serves the last validated snapshot with visible age;
- import-source failure leaves local progress unchanged;
- parse failure quarantines the incoming document and applies nothing;
- database failure rolls back both state and journal;
- `401/403` disables scheduled sync until the user reconnects;
- `429` honors server backoff and shows next eligible refresh;
- a mode mismatch blocks import; and
- a catalog mismatch keeps unknown IDs as unresolved evidence.

## Staged implementation

### Stage 0 — decision and source contract

- Accept this proposal through an ADR before implementation.
- Record the exact static task/map fixture revision and supported objective
  taxonomy.
- Confirm distribution/attribution rules for the static payload and each map
  asset family.
- Define the local JSON v2 schema and migration ownership.

Exit: accepted ADR, reviewed schema, attribution plan, and no unresolved safety
exception.

### Stage 1 — catalog fidelity

- Add a forward SQLite migration for prerequisites, failures, objective maps,
  zones, optionality, snapshot provenance, and orphan tracking.
- Expand static DTO normalization with an unsupported-objective fallback.
- Preserve raw JSON and translation behavior.
- Build catalog queries without modifying profile progress.

Exit: the pinned task fixture imports transactionally; all current objective
types are either normalized or intentionally preserved as unsupported; a
second identical refresh is idempotent.

### Stage 2 — owned local tracker

- Add task/objective commands, profile/mode generation, journal, derived
  eligibility, and quest/item read models.
- Upgrade profile JSON through backward-compatible version handling.
- Compose services into the running application.

Exit: a user can create a profile, manually record task/objective progress,
restart offline, and receive the same active quest and item-need results.

### Stage 3 — second-screen quest and map UX

- Implement quest board/detail and item links.
- Add the static objective layer to approved map variants.
- Add age, source, mode, unsupported, and unprojectable states.

Exit: every visible marker traces to a task/objective/zone plus transform and
source; missing geometry never becomes an invented marker.

### Stage 4 — owned import/export and reconciliation

- Implement project JSON v2, preview, conflicts, journaled apply, and undo.
- Migrate project JSON v1 without manufacturing unknown task state.
- Add strict resource limits and atomic file behavior.

Exit: round trip is lossless for owned fields; malformed and wrong-mode files
leave the database unchanged; import is idempotent and reversible.

### Stage 5 — optional TarkovTracker read adapter

- Add secure connect/disconnect, token/mode validation, supported GET calls,
  ETag/quota handling, and manual/foreground refresh.
- Reuse the same preview/reconciliation boundary.
- Keep the adapter feature-disabled when secure storage is unavailable.

Exit: fixtures prove no HTTP method other than GET, no non-allowlisted host,
no credential-bearing redirect, no token leak, and no local regression without
explicit user confirmation.

### Stage 6 — hardening and release validation

- Exercise full-size current static data and sanitized external snapshots.
- Run accessibility, performance, offline, recovery, privacy, license, and
  safety reviews.
- Document any deliberately unsupported objective/map variants.

Exit: build/test scripts pass, the release report distinguishes implemented
from planned behavior, and no external write or game interaction exists.

## Test strategy

### Domain unit tests

- eligibility across complete/active prerequisites, level, faction, delays,
  missing data, and cycles;
- task status versus objective status and recorded-objectives-satisfied;
- found versus hand-in objectives, FIR versus non-FIR, acceptable item sets,
  optional objectives, and remaining counts;
- failure/restart branches and explicit reset behavior;
- multiple/no map associations and projection-unavailable results;
- exact mode/generation isolation; and
- unknown objective/status values remain observable.

### Catalog contract tests

- pinned fixtures for every currently observed objective discriminator;
- translation keys, empty translations, unknown fields, and missing required
  fields;
- multiple maps, zone outlines/elevation, keys, failure conditions,
  restartability, and prerequisite statuses;
- ETag/Last-Modified/304, bounded retry, cancellation, stale fallback, and
  atomic refresh; and
- schema drift adds a diagnostic/unsupported record without losing the prior
  valid snapshot.

### Persistence tests

- forward migration from the current database;
- refresh does not erase or cross-link profile progress;
- task/objective command and journal commit/rollback atomically;
- orphan progress survives a catalog change and resolves later;
- UTC precision and deterministic read ordering; and
- local JSON v1 migration plus v2 round trip and atomic replacement.

### Reconciliation property and scenario tests

- applying the same snapshot twice is idempotent;
- preview invalidates when local base revision changes;
- absent source records never delete local state;
- monotonic promotions, count increases, regressions, complete/failed, and
  restart/reset permutations;
- explicit local corrections are not silently reverted;
- undo restores values as a new audited change;
- invalid, duplicate, and unknown external IDs are quarantined; and
- wrong-mode/generation input can never apply.

### TarkovTracker adapter tests

- only the canonical HTTPS origin and supported public routes are reachable;
- only GET is exposed/sent, even under malformed input;
- Authorization is redacted and never forwarded on redirect;
- current token prefixes/permissions and response mode are validated;
- User-Agent, ETag, 304, 401/403, 429/`Retry-After`, 5xx, timeout,
  cancellation, and daily quota handling;
- a 304 consumes quota in the scheduler model;
- public response fields map without treating `invalid` as completion; and
- token data never appears in logs, exports, exceptions, or snapshots.

### UI and safety tests

- quest/source/catalog age labels cannot be confused;
- active filters and item quantities update after a committed command/import;
- map markers require approved transform and source provenance;
- unsupported or unprojectable objectives have accessible fallbacks;
- no game-window overlay, process-memory API, injection/hook library,
  packet-capture/raw-traffic dependency, gameplay input synthesis, or external
  progress mutation enters the dependency graph; and
- an offline run supports all local tracking workflows.

Run `scripts/build.sh`, `scripts/test.sh`, and the existing license/safety
checks for each substantive implementation stage. Live API checks complement
fixtures but cannot be the only acceptance evidence.

## Risks and mitigations

### Upstream schema drift

New objective variants or changed fields can break a closed parser. Preserve
raw JSON, tolerate unknown fields, quarantine unsupported variants, pin fixture
revisions, and publish diagnostics without discarding the last valid snapshot.

### Incorrect derived status

Complex branches, delays, failure, and hidden game conditions can make a simple
"ready" claim wrong. Keep recorded state separate from derived eligibility,
use indeterminate results, list the inputs, and say "recorded objectives
satisfied" instead of claiming live readiness.

### Cross-mode/wipe contamination

IDs may be shared while progress is not. Key every mutable operation by local
profile, exact game mode, and profile generation. Block ambiguous seasonal or
legacy-token imports.

### Stale external snapshots

Fetch time is not edit time, and profile pages may refresh only after human
interaction. Never use fetch time as last-write-wins, label age/source, ignore
missing records as deletions, and preview regressions.

### Credential leakage

Redirects, logs, exports, crash reports, and broad HTTP abstractions are common
leak paths. Use an origin allowlist, redirect rejection, secret handles,
redaction-by-construction, narrow GET-only methods, and leak-focused tests.

### Rate-limit or service dependency

Aggressive polling can exhaust the documented free allocation and an outage
could strand an external-first design. Poll only at startup/manual/foreground
intervals, honor quota, and keep local progress/cached catalogs fully usable.

### False map precision

Task map IDs, world zones, and community map projections can disagree. Require
a reviewed transform for the exact map variant, validate bounds/layers, and
fall back to a map-level association rather than fabricating a marker.

### License or attribution regression

Open APIs, GPL source, MIT configuration, and restricted art are different
license surfaces. Implement clients clean-room, keep source code out, attach
provenance to assets/data, and block bundling until the exact license is
approved.

### Architecture overreach

A full bidirectional sync engine would add distributed-state complexity and
external side effects without v1 value. Limit v1 to local commands and
one-way snapshots through one import planner. Revisit writes only through a
new safety/product decision.

## Decision record

**Status:** Proposed.

**Decision:** Tarkov Companion owns canonical quest progress locally. It uses
mode-specific `json.tarkov.dev` task/map documents as the production catalog,
treats GraphQL as a development-time schema oracle only, supports its own
versioned JSON exchange, and may add TarkovTracker as an optional read-only
snapshot source through a secure GET-only adapter. Imports are previewed,
provenance-bearing, mode/generation-scoped, journaled, and reversible. Static
quest geometry can be shown only on approved second-screen maps with validated
transforms.

**Consequences:** The app works offline and without accounts; progress remains
private and editable; external users can avoid re-entry; and ambiguous merges
are surfaced rather than guessed. The cost is new normalized catalog tables,
progress/audit records, a reconciliation boundary, secure secret storage, and
explicit unsupported states. No external write-back means edits made in Tarkov
Companion do not appear in TarkovTracker.

**Rejected:** External-first canonical state, local-only forever, runtime
GraphQL fallback, implicit newest-fetch-wins, write-back in v1, and automatic
game observation.

**Revisit when:** TarkovTracker publishes a stable versioned interchange
contract with per-field revisions; product requirements explicitly demand
write-back and a new safety/privacy review accepts it; Battlestate publishes an
authorized progress API; or the static catalog/asset license or supported
endpoint guidance materially changes.

## Implementation acceptance criteria

The first implementation is complete only when:

1. local manual task/objective tracking works after restart and offline;
2. profile progress is isolated by exact mode and generation;
3. current task data imports without losing prerequisites, maps, item/FIR,
   failure/restart, or unsupported objective evidence;
4. active quest/item/map read models are derived from recorded state with
   source and UTC age;
5. no unvalidated objective geometry is presented as an exact marker;
6. project JSON import/export is atomic, secret-free, previewed, and tested;
7. optional TarkovTracker use is GET-only, canonical-host-only, quota-aware,
   securely stored, and nonessential;
8. ambiguous or regressive imports require user resolution and remain
   auditable/reversible;
9. Core remains independent of Avalonia, Windows, SQLite, HTTP, capture, and
   filesystem watchers; and
10. build, test, license, and safety checks pass with no feature claims beyond
    the composed runtime.

## Sources

[^1]: TarkovTracker, “API Integration” and “Systems,” current revision
    `3e8f036cf3582684a3dc4cbcb7ef3396357ad14c`, accessed 2026-09-10:
    [API.md](https://github.com/tarkovtracker-org/TarkovTracker/blob/3e8f036cf3582684a3dc4cbcb7ef3396357ad14c/docs/API.md) and
    [SYSTEMS.md](https://github.com/tarkovtracker-org/TarkovTracker/blob/3e8f036cf3582684a3dc4cbcb7ef3396357ad14c/docs/SYSTEMS.md).

[^2]: tarkov.dev, “JSON API Endpoints,” accessed 2026-09-10:
    [endpoint catalog](https://json.tarkov.dev/endpoints).

[^3]: tarkov.dev, regular-mode task dataset, live snapshot inspected
    2026-09-10 (HTTP `ETag` and `Last-Modified` recorded during research):
    [tasks](https://json.tarkov.dev/regular/tasks) and
    [English translations](https://json.tarkov.dev/regular/tasks_en).

[^4]: tarkov.dev, regular-mode map dataset, live snapshot inspected
    2026-09-10: [maps](https://json.tarkov.dev/regular/maps).

[^5]: the-hideout/tarkov-dev, map configuration, revision
    `f4be309c40a895caca652e08169f3438d04d00a6`, accessed 2026-09-10:
    [src/data/maps.json](https://github.com/the-hideout/tarkov-dev/blob/f4be309c40a895caca652e08169f3438d04d00a6/src/data/maps.json).

[^6]: the-hideout/tarkov-api, static GraphQL schema, revision
    `92f40c6ecc32dcfab67cb2ff9a00c864c54b79a3`, accessed 2026-09-10:
    [schema-static.mjs](https://github.com/the-hideout/tarkov-api/blob/92f40c6ecc32dcfab67cb2ff9a00c864c54b79a3/schema-static.mjs).

[^7]: the-hideout/tarkov-api, GraphQL examples, revision
    `92f40c6ecc32dcfab67cb2ff9a00c864c54b79a3`, accessed 2026-09-10:
    [docs/graphql-examples.md](https://github.com/the-hideout/tarkov-api/blob/92f40c6ecc32dcfab67cb2ff9a00c864c54b79a3/docs/graphql-examples.md).

[^8]: Escape from Tarkov Wiki, “Found in raid,” accessed 2026-09-10:
    [Found in raid](https://escapefromtarkov.fandom.com/wiki/Found_in_raid).

[^9]: Escape from Tarkov Wiki, “First in Line,” accessed 2026-09-10:
    [First in Line](https://escapefromtarkov.fandom.com/wiki/First_in_Line).

[^10]: TarkovTracker, project README, revision
    `3e8f036cf3582684a3dc4cbcb7ef3396357ad14c`, accessed 2026-09-10:
    [README.md](https://github.com/tarkovtracker-org/TarkovTracker/blob/3e8f036cf3582684a3dc4cbcb7ef3396357ad14c/README.md).

[^11]: TarkovTracker, “API Integration,” canonical host, redirects, cache and
    quota guidance, revision `443d9fd73f0f88cac1623206fe79ba122ab9b1fb`,
    accessed 2026-09-10:
    [docs/API.md](https://github.com/tarkovtracker-org/TarkovTracker/blob/443d9fd73f0f88cac1623206fe79ba122ab9b1fb/docs/API.md).

[^12]: TarkovTracker, public progress OpenAPI source, revision
    `443d9fd73f0f88cac1623206fe79ba122ab9b1fb`, accessed 2026-09-10:
    [workers/api-gateway/src/openapi.ts](https://github.com/tarkovtracker-org/TarkovTracker/blob/443d9fd73f0f88cac1623206fe79ba122ab9b1fb/workers/api-gateway/src/openapi.ts).

[^13]: TarkovTracker, backup composable, revision
    `3e8f036cf3582684a3dc4cbcb7ef3396357ad14c`, accessed 2026-09-10:
    [app/composables/useDataBackup.ts](https://github.com/tarkovtracker-org/TarkovTracker/blob/3e8f036cf3582684a3dc4cbcb7ef3396357ad14c/app/composables/useDataBackup.ts).

[^14]: TarkovTracker, architecture and public profile import behavior,
    revision `3e8f036cf3582684a3dc4cbcb7ef3396357ad14c`, accessed
    2026-09-10:
    [docs/ARCHITECTURE.md](https://github.com/tarkovtracker-org/TarkovTracker/blob/3e8f036cf3582684a3dc4cbcb7ef3396357ad14c/docs/ARCHITECTURE.md) and
    [docs/SYSTEMS.md](https://github.com/tarkovtracker-org/TarkovTracker/blob/3e8f036cf3582684a3dc4cbcb7ef3396357ad14c/docs/SYSTEMS.md).

[^15]: the-hideout/tarkov-api, GPL-3.0 license, revision
    `92f40c6ecc32dcfab67cb2ff9a00c864c54b79a3`, accessed 2026-09-10:
    [LICENSE](https://github.com/the-hideout/tarkov-api/blob/92f40c6ecc32dcfab67cb2ff9a00c864c54b79a3/LICENSE).

[^16]: TarkovTracker, GPL-3.0 license, revision
    `3e8f036cf3582684a3dc4cbcb7ef3396357ad14c`, accessed 2026-09-10:
    [LICENSE.md](https://github.com/tarkovtracker-org/TarkovTracker/blob/3e8f036cf3582684a3dc4cbcb7ef3396357ad14c/LICENSE.md).

[^17]: the-hideout/tarkov-dev, MIT license, revision
    `f4be309c40a895caca652e08169f3438d04d00a6`, accessed 2026-09-10:
    [LICENSE](https://github.com/the-hideout/tarkov-dev/blob/f4be309c40a895caca652e08169f3438d04d00a6/LICENSE).

[^18]: the-hideout/tarkov-dev-svg-maps, asset license and usage conditions,
    revision `5a8b6115d1c0cf56f2ebaac1a96fa5ae3074d178`, accessed
    2026-09-10:
    [README.md](https://github.com/the-hideout/tarkov-dev-svg-maps/blob/5a8b6115d1c0cf56f2ebaac1a96fa5ae3074d178/README.md).

[^19]: tarkov.dev, API availability statement, accessed 2026-09-10:
    [API page](https://tarkov.dev/api/).

[^20]: Escape from Tarkov Wiki, “Quests,” accessed 2026-09-10:
    [Quests](https://escapefromtarkov.fandom.com/wiki/Quests).

[^21]: Escape from Tarkov Wiki, “The Ticket,” accessed 2026-09-10:
    [The Ticket](https://escapefromtarkov.fandom.com/wiki/The_Ticket).
