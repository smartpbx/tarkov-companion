# Architecture

## Dependency direction

```text
App ────────────────┐
Platform.Windows ───┼──> Application ──> Core
Infrastructure ─────┘         │
                              └── contracts implemented by outer layers
EftSimulator ─────────────> App/Application test seams
GroupServer ──────────────> Core            (a second executable; see "The group relay")
```

`Core` contains immutable domain records, deterministic calculations, and boundary interfaces that do not require platform or I/O packages. `Application` coordinates use cases. `Infrastructure` owns HTTP, SQLite, caching, OCR/image recognition, and optional external progress import. `Platform.Windows` owns ordinary Windows window discovery, capture, monitors, path discovery, watchers, and secrets. It owned a global hotkey and no longer does: the window's own key bindings fire only when the companion has focus, which is the correct behaviour beside a fullscreen game. `App` owns Avalonia views and ViewModels only.

## Runtime flow

`AppComposition` is the single composition root for the normal GUI, demo GUI, diagnostics, and self-test. It resolves the application-data directories, persistent SQLite database, HTTP/cache and normalized repositories, profile/intelligence/raid/strategy services, interactive map services, logging, runtime coordinators, scan seam, and service-backed ViewModels. Platform-specific implementations are registered only on Windows.

Startup applies the hand-written migrations, seeds deterministic local data only in demo mode, loads the local profile and usable cached records, and publishes an observable runtime snapshot before starting any refresh. When local data is stale, the coordinator performs a bounded forced refresh on a background task; each successful endpoint remains transactionally independent. `TARKOV_COMPANION_OFFLINE=1` substitutes a rejecting HTTP handler, skips refresh, and labels either the existing cache or its absence honestly.

Each page's own startup load runs on its own. Migrations and the database are the one genuine prerequisite; the ten page loads after them — items, quests, history, scanner, hideout, ammo, keys, events, group, map — are siblings, not a chain. They were a single await chain in one try block, so a failure in any one of them skipped every step after it for the rest of the session and left several pages that never filled in until the application was restarted. One that fails now fails alone, is logged, and is named in `MainWindowViewModel.StartupFaults`; every page has its own Reload, so it is recoverable without a restart.

A plain launch opens the V2 shell (`V2ShellView` over `V2ShellViewModel`, variant A). `--ui-shell legacy` opens the V1 shell instead, and `v2-b` the other V2 navigation; the choice is made once at process start and never swapped, because hot-swapping would mean two runtime graphs over one database and one screenshot watcher. The two shells are siblings under `MainWindow`, each realised only when it is the one running: the V1 shell is `Views/Pages/LegacyShellView.axaml`, hosted through a template whose content is null under V2, so a V2 launch does not construct V1's fourteen pages. Five V2 workspaces (Ammo, Keys, Flea, Loadout, Events) are V2 views over V1's page view models, so those view models are shared rather than duplicated. `docs/V1_PARITY_LEDGER.md` records, per V1 capability, where it lives in V2 and what proves it.

The UI consumes runtime snapshots and repositories rather than constructing a second fake application model. Normal and demo modes use the same commands and ViewModels. Demo mode changes only the registered fixture adapter and deterministic seed, while unavailable data, map state, position, or scans are presented as unavailable. Application shutdown cancels and awaits startup/map work before disposing the service provider.

Profiles are first-class (#269). The profile workspace (`ProfileContextService`) says which profile is active and carries its game mode, wipe and language; `ProfileRuntimeContextService` publishes that as one revisioned snapshot. Two things read it. `ApplicationStartupCoordinator` fetches the catalog for the active profile's mode and language, and fetches again when a switch changes either, instead of using the `RuntimeOptions` Regular/`en` defaults; with no profile at all (a V1 launch) it still uses those defaults, and a profile whose mode is unknown fetches nothing. `ProfileScopedPlayerProfileService` implements `IPlayerProfileService` over one progress file per profile: the profile that already existed keeps `profile.json`, every other profile has `profiles/<id>.json`, and a write that carries another profile's id is refused. Setup › Game & Profile creates, switches, archives and restores them through `ProfileManagementService`.

The quest view also composes the project-owned JSON v2 exchange service. Infrastructure performs bounded, checksummed, atomic local file I/O; Application selects one exact profile/mode/generation scope and classifies monotonic, conflicting, unchanged, and unresolved proposals; SQLite applies a confirmed preview and its inverse journal in one transaction. The UI never parses JSON or merges records itself, and project exchange performs no network access.

The same import planner accepts the optional TarkovTracker snapshot adapter. On Windows, composition makes the disconnected feature available when its narrow DPAPI current-user store is available, unless explicitly opted out; offline and unavailable-store states remain visible and inert. Composition and status checks never use HTTP. Explicit Connect and Refresh actions are the only network entry points, and the Infrastructure client can issue only canonical-host `GET /token` and `GET /progress` with redirects disabled. External snapshots keep fetch provenance but no manufactured source edit time or generation, then cross the same confirmation, atomic journal, and undo boundary as project JSON.

A user scan captures visible pixels into memory, detects a context, obtains OCR/icon candidates, resolves canonical item or extract IDs, and only then invokes recommendation/economy services. Capture bytes are discarded by default.

The executable composes `IScanUseCase` through an `IScanAdapter` seam. Demo mode registers a deterministic adapter that resolves a seeded item through the real item and recommendation services. Windows registers `RecognitionScanAdapter` over the real recogniser — the one built into Windows where it is present, Tesseract otherwise, and Settings says which is actually running. Everything else registers an honest unavailable adapter rather than a silent no-op. The authenticated developer diagnostic channel invokes this same use case.

Game logs and screenshot filenames are independent, evidence-based inputs to the raid state. Screenshot filenames update only the player's last-known position and always carry freshness. The strategy engine consumes public/static map inputs and never consumes enemy observations.

`RaidActivityCoordinator` records raid starts, evidence transitions, positions, extracts, successful scans, and raid completion through `IRaidHistoryService`. History stores structured JSON event payloads and summary rows only; captured pixels are never persisted.

## Planning

What to bring, build and keep is decided in `Application/Services/Planning` and answered in the records of `Core/Domain/Planning`, not in the Plan view models that present it. `QuestRequirementPlanner` turns objectives and holdings into Bring / Hand in / Find in raid requirements; `HideoutPlanner` gives each station's next level, what can be started now and what is short across all of them; `KeepListPlanner` (fed by `KeepListService`) computes the Keep list. They take one snapshot and are deterministic for it: no clock, no catalog reads, ordered by id rather than by a display name. The view models add names and words. Typed local event rules are parsed in Application, preserve their definition provenance, and feed recommendation pricing and next-raid map availability. Prerequisite graphs, route bundles and craft/barter chains are not part of this yet (#307 says which exist elsewhere and which do not).

## The group relay

`TarkovCompanion.GroupServer` is a second executable, an ASP.NET minimal-API application that
depends on `Core` and nothing else in this solution. It is deployed on its own and updates
itself; the client works completely without it and sends nothing while group sharing is off.

Four things live there, and the reason they live there rather than in the client is the same
each time: they are the parts that only make sense between people.

**The room.** A group agrees one reusable key. The relay receives it on each request, hashes it,
and buckets members by that hash. Stock source does not intentionally log or persist the raw key,
but the transport and receiving process can read it. Members publish themselves and are answered
with everyone else in one exchange, so there is no subscription to hold open and a companion that
is not running shows nothing. A
member is forgotten three minutes after they stop publishing, and observations about anybody
outside the room are pruned on the way in *and* on the way out, because the game describes
every member of an in-game party and a five-man filled from matchmaking carries a stranger.

**Marks.** Waypoints are a plan and persist; pings mean "look here" and expire in forty-five
seconds, so one restored from disk would be a lie. They ride along on the exchange the client
already makes, rather than a second endpoint to poll.

**The catalog mirror.** One copy of the game data for the whole group instead of five clients
each pulling the same several megabytes from upstream, content-addressed with an ETag and held
compressed at rest. The client tries the relay first and falls back to upstream, which is what
keeps the relay an optimisation rather than a dependency.

**Problem reports.** The client posts what it knows about itself; the relay keeps it and hands
back a reference. An hourly workflow is designed to open an issue naming only a validated
reference, but currently fails closed on the list/read reference mismatch assigned to #310. The
relay holds no GitHub credential—the workflow files issues with the token Actions gives it—which
matters because the relay is the internet-facing box.

Two access models, deliberately separate. A group key is proof of belonging to one room and
every member of every group holds one. An operator secret (`TARKOV_RELAY_ADMIN_KEY`) is what
reads every group's reports, registers which rooms may exist, and asks the relay to update; no
group key can do any of that. Registering the first room closes the relay to unregistered ones,
and until one is registered it is open, which is what it has always been.

Persistent state lives outside the tree the updater replaces. The relay's writable directory
holds marks, the room registry, submitted problem reports, and the transient update request;
authenticated updater history and panel status live in separate root-owned directories. Live
member state is not written there, but a reached waypoint records who reached it and a report
body from an arbitrary relay caller can carry coordinates even though the ordinary desktop's
closed report projection does not.
The relay cannot start a systemd unit and must not be able to — asking it to update writes a file
that a `.path` unit watches, and the updater ships its own units inside the archive so a fix to
them reaches the box.

## Times the player reads

Storage, the protocol, the database, the logs, and the checksummed exchange envelopes (profile,
quest-progress, and stash-snapshot documents) hold UTC and never change. Every absolute time a
person reads is that instant shown in the player's own zone, and only
`TarkovCompanion.Core.Common.LocalTime` does the conversion. A view model calls `LocalTime.Moment`,
`Time`, `ShortTime`, or `Date` (the player's culture) or `Sortable` / `SortableSeconds` (a fixed
order for diagnostics); it never calls `ToLocalTime`, prints `:u`, or writes "UTC" after a time.
Relative times ("4h ago") stay relative and are computed from two UTC instants.

Output that leaves the screen says which clock it uses. The raid-history CSV is opened in a
spreadsheet, so it carries the local clock under `start_local` / `end_local` headers; the JSON is
ISO-8601 at the local numeric offset, so a program reads the identical instant. Copied diagnostics
name the player's offset once in the header. Tests pin a zone that is never UTC
(`LocalTime.UseZone`), because a UTC-only CI box prints local and UTC identically and hid this bug;
`LocalTimeRuleContractTests` fails when a call site goes around the helper.

## Cross-platform contract

Linux must build and test all domain, application, data, recognition, simulator, and demo behavior. Windows-specific code is guarded behind interfaces and runtime OS checks. The self-contained `win-x64` publish is produced on Linux and proven in the Windows VM with synthetic permitted inputs.
