# Persistence and local-state adversarial review

**Issue:** [#317](https://github.com/smartpbx/tarkov-companion/issues/317)

**Reviewed source:** `d59d136` (2026-09-14); static, read-only source/test review. No build or test was run.
**Scope:** SQLite, migrations, local JSON/files, profile and quest exchange, raid history/export,
settings, screenshot retention, and the local state machines that write them. The companion remains
external and read-only relative to EFT; this review found no change to that boundary.

## Method and boundary

This is an adversarial V1 baseline, not evidence that a proposed V2 component exists. The reviewer
read every migration under `Infrastructure/Persistence/Migrations`, the SQLite connection/migration
runner and repositories, profile/exchange serializers, JSON settings stores, event and map-preference
file stores, raid outbox/history/retention services, and the focused persistence/profile/history/settings
tests. `docs/DATABASE.md`, ADRs 0003--0006, `docs/TESTING.md`, and the existing #317 baseline were
then reconciled against source.

Source locations below are exact **Reviewed** evidence. “Covered” means a named current test appears
to exercise the ordinary property; it never means that test ran or that fault injection exists unless
stated. No exact run was observed, so no row is **Tested (automated)**. Required tests are deterministic
tests to add before the listed disposition can be changed. A same-user process that can change application
data is in scope: `%LOCALAPPDATA%`, portable install locations, user-selected import/export roots, and
EFT screenshot roots are not trusted merely because they are local.

## What V1 demonstrably controls

| Surface | Current source fact and evidence | Named test coverage (not run) | Residual boundary |
| --- | --- | --- | --- |
| SQLite writes | Every opened connection enables FK enforcement, WAL, and a five-second busy timeout (`SqliteDatabase.cs:31-45`). Individual embedded migrations execute with their `schema_migrations` record in one transaction (`SqliteMigrationRunner.cs:184-208`); catalog replacement helpers and quest changes similarly transact. | `SqliteMigrationTests.cs:43-56`; malformed item refresh preserves prior rows in `SqliteDataPersistenceTests.cs:91-130`; quest persistence/exchange integration suites. | This is atomicity within a successful SQLite transaction, not corruption recovery, multi-process coordination, or downgrade safety. |
| Quest exchange | Version 2 JSON is bounded (2 MiB, depth 32, eight profiles, 50,000 records), checksummed canonical data, exact scope validated, preview/revision checked, and apply/undo journaled in one SQLite transaction (`ProjectQuestProgressJson.cs:13-59`, `SqliteQuestProgressImportStore.cs:31-186,477-835`). | `QuestProgressExchangeTests.cs`; `QuestImportHistoryTests.cs`; `TarkovTrackerIntegrationTests.cs`. | This protection covers the separate quest exchange format, not the active `profile.json` import or raid history export. |
| Ordinary JSON replacement | Profile and quest exports write a sibling temporary file and rename it (`JsonFilePlayerProfileService.cs:111-140`, `ProjectQuestProgressJson.cs:115-161`). Settings use the shared temporary-and-move helper (`AtomicJsonFile.cs:21-54`). | `AtomicJsonFileTests.cs:19-39`; profile round-trip/migration cases in `ProfilePersistenceTests.cs`. | Rename prevents an ordinary half-written destination, but not durable-directory guarantees, cross-process writer races, or path substitution. |
| Destructive screenshot action | V1 only chooses matching screenshot names, keeps newest, skips cloud/reparse files, and asks the Windows recycle bin rather than deleting (`ScreenshotRetention.cs:76-151`). | `ScreenshotRetentionServiceTests.cs:26-122`. | Eligibility is evaluated by pathname/metadata, not a stable opened handle; V1 defaults it on. |
| Time representation | Writers call `ToUniversalTime().ToString("O")` in migration, progress, data refresh, raid and scan paths; quest exchange requires an explicit zero offset (`SqliteMigrationRunner.cs:204-208`, `SqliteQuestProgressStore.cs:38-71`, `ProjectQuestProgressJson.cs:855-864`, `SqliteRaidHistoryService.cs:393-394`). | UTC price and scan assertions: `SqliteDataPersistenceTests.cs:71-89`, `RecognitionPersistenceTests.cs:29-58`. | Several reads use throwing `DateTimeOffset.Parse`; a corrupt persisted timestamp can still abort a list/startup path. |

## Findings

### PERS-01 — a downgraded binary can continue applying migrations to a newer database

- **Severity:** High.
- **Source evidence:** `SqliteMigrationRunner.ApplyAsync` reports unknown recorded versions but does
  not refuse writes (`SqliteMigrationRunner.cs:45-75`); it still applies every known migration absent
  from `schema_migrations`. The only current newer-version test asserts reporting, not a safe
  read-only/recovery mode (`SqliteMigrationTests.cs:116-151`).
- **Concrete scenario:** Install build N+1 which records a new migration, then launch build N whose
  embedded list lacks it but still has an older migration missing from the recorded table (or whose
  resource ordering differs). Build N can run that SQL against a schema it does not understand.
  The result may be failed startup, schema divergence, or data loss from a destructive old migration.
- **Current control/evidence:** Each *one* migration and its ledger row commit together; pre-migration
  `VACUUM INTO` backup is attempted for an existing database (`SqliteMigrationRunner.cs:56-60,97-135`).
- **Residual risk:** The comment treats availability as preferable to refusal, but an unknown schema is
  a compatibility boundary, not merely an informational state. There is no compatibility fence,
  integrity check, restore command, or downgrade test.
- **Disposition / owner:** **Mitigate; #270.** V1 fact, not an implementation of #270’s backup/restore
  acceptance criterion.
- **Required deterministic test:** Create a fixture DB recording a synthetic future version and a
  deliberately missing known version; assert startup enters explicit read-only/recovery state, applies
  no SQL, leaves schema/data byte-equivalent, and exposes the backup/restore option.

### PERS-02 — migration backup failure and pruning are silent, bounded only by file count

- **Severity:** Medium.
- **Source evidence:** Backup exceptions return `null` and migration continues
  (`SqliteMigrationRunner.cs:93-95,121-135`). Pruning deletes all but two backups based on mutable
  `LastWriteTimeUtc` and suppresses I/O/auth failures (`SqliteMigrationRunner.cs:138-155`).
  `MigrationBackupTests.cs` proves a happy-path `VACUUM INTO` copy, not no-space, interruption,
  restore, retention, or sidecar races.
- **Concrete scenario:** A full disk or denied backup directory prevents the only pre-destructive copy;
  migration 0007/0009/0010 then executes. Or altered timestamps select the wrong two backups for
  deletion. A user has neither an explicit warning nor an in-product recovery path.
- **Current control/evidence:** WAL-aware `VACUUM INTO` is correctly chosen rather than copying only
  the main DB (`SqliteMigrationRunner.cs:82-91`), and each migration is transactional.
- **Residual risk:** Transaction rollback does not restore a successfully committed destructive
  migration, and backup availability is not a release-evidenced property.
- **Disposition / owner:** **Mitigate; #270.** Current V1 behavior; #270 is planned work, not current
  recovery capability.
- **Required deterministic test:** Inject `SQLITE_FULL`, `UnauthorizedAccessException`, cancellation
  during backup, and prune timestamp ties; assert destructive migration is blocked or an actionable
  recoverable state names a verified backup. Then inject post-backup migration failure and prove restore
  preserves representative raid, profile-progress, and cache rows.

### PERS-03 — active profile import is a blind replacement with no freshness, scope, or recovery fence

- **Severity:** High.
- **Source evidence:** `ImportJsonAsync` parses then immediately calls `SaveAsync`
  (`JsonFilePlayerProfileService.cs:96-106`). It accepts schema 1/2 and normalizes a legacy generation
  (`lines 143-168`); neither exported timestamp nor current active ID/mode/generation/revision is
  compared. A parse/oversize error propagates from `GetActiveAsync` (`lines 53-63`) and the profile
  file has no `SetAside`/backup path.
- **Concrete scenario:** A stale profile export from another wipe, mode, or person is imported after
  preview elsewhere in the UI. It replaces `Config/profile.json`, changing profile ID/generation while
  quest progress and raid rows remain separately scoped in SQLite. A later retry/replay is indistinct
  from an intentional replacement; a corrupted stored profile prevents normal recovery.
- **Current control/evidence:** File size, schema range, required fields, levels/counts, sorted
  collections, and UTC normalization are validated (`JsonFilePlayerProfileService.cs:11-16,143-240`).
  Write uses a temporary file and one process-local semaphore. `ProfilePersistenceTests.cs` covers
  schema/round-trip validation.
- **Residual risk:** V1 has one active profile but still exposes ID/mode/generation in its serialized
  form. The atomic file replacement cannot make the independent profile/SQLite stores one atomic
  state. No user-visible quarantine, timestamp/replay decision, or restore exists.
- **Disposition / owner:** **Mitigate; #269 with #270 persistence contract.** Do not describe #269’s
  multiple-profile/context model as shipped V1.
- **Required deterministic test:** Given active profile A plus SQLite rows scoped to A, import an older
  B, wrong-mode B, wrong-generation A, and malformed stored document; assert explicit conflict or
  quarantine, no mutation of either store before confirmation, recoverable corrupt-file handling, and
  no cross-scope history/progress presentation after accepted switch.

### PERS-04 — unreadable retention settings silently enable destructive cleanup

- **Severity:** Low. The behavior mutates player-owned files, but filename selection, keep-newest,
  reparse/cloud exclusions, and recycle-bin recovery bound the current impact; this matches
  `RISK-SCREENSHOT-RETENTION-DEFAULT` and DIAG-07.
- **Source evidence:** `ScreenshotRetentionSettings.Default` is enabled at 24 hours
  (`ScreenshotRetention.cs:10-23`); missing, oversized, malformed, unreadable, or unauthorized
  `screenshots.json` returns that default (`JsonFileScreenshotRetentionStore.cs:27-35,61-79`). The
  implementation explicitly chooses this fail-open direction. The default is documented in the
  current #317 system boundary baseline.
- **Concrete scenario:** A partial write, disk error, or local modification makes the setting unreadable.
  The next tidy cycle recycles game-created screenshots although the user may have disabled cleanup;
  the behavior is indistinguishable from a deliberate enabled setting.
- **Current control/evidence:** Files are sent to recycle bin, newest/matching/cloud-only exclusions
  apply, and retention hours clamp (`ScreenshotRetention.cs:102-129`; test coverage cited above).
- **Residual risk:** Recycle-bin recovery is not a consent/recovery ledger. There is no set-aside,
  notification, preview, exact file list, or default-off migration.
- **Disposition / owner:** **Mitigate; #309.** This is a current V1 fact; #309’s default-off,
  inspectable, recoverable design is planned.
- **Required deterministic test:** Corrupt, remove, oversize, and deny the settings file after a saved
  disabled state; assert cleanup remains off, state reports recovery needed, and no recycle call occurs.

### PERS-05 — local JSON writers do not defend against cross-process races or path substitution

- **Severity:** Medium.
- **Source evidence:** `AtomicJsonFile` always uses `<path>.writing`, `FileMode.Create`, and a move
  (`AtomicJsonFile.cs:21-54`); map preferences similarly use a fixed `.tmp`
  (`JsonFileMapVariantPreferenceStore.cs:91-126`). Semaphores in the stores are instance-local, while
  `Path.GetFullPath` is the only path validation in the profile and exchange serializers
  (`JsonFilePlayerProfileService.cs:11-16`; `ProjectQuestProgressJson.cs:911-915`).
- **Concrete scenario:** Two app instances or an editor/process replace the config/export path or its
  parent between validation and move. A fixed scratch name can collide; last writer wins or an
  unrelated local target is written/replaced. A symlink/junction root changes the effective destination
  outside the intended app/export directory.
- **Current control/evidence:** Same-process operations serialize per service instance; writers use
  sibling temporary files, and quest/profile export temp names include GUIDs. `AtomicJsonFileTests.cs`
  proves no normal scratch residue, not concurrent or reparse behavior.
- **Residual risk:** `GetFullPath` canonicalizes syntax only. It neither proves ownership/containment
  after resolution nor holds a stable file handle; `FlushAsync` without `Flush(true)` in
  `AtomicJsonFile.cs:40-46` also lacks explicit durable-file and durable-directory evidence.
- **Disposition / owner:** **Mitigate; #270** for local storage primitives; **#309** for screenshot
  cleanup roots. Existing V1 writers must not be credited with #270’s recoverability guarantee.
- **Required deterministic test:** Two-process (or deterministic barrier) writers, cancellation during
  write/move, existing scratch collision, parent/file symlink or junction replacement, and destination
  outside-root attempts; assert no target outside policy changes, exactly one valid document remains,
  and the loser receives a visible conflict/failure.

### PERS-06 — map preference corruption is silently converted into loss of all preferences

- **Severity:** Low.
- **Source evidence:** Map preferences map `InvalidDataException`/`JsonException` to an empty map
  (`JsonFileMapVariantPreferenceStore.cs:47-57`). A following `SetAsync` writes only the new key
  (`lines 30-44,91-126`), overwriting the malformed original without set-aside or notification.
- **Concrete scenario:** A truncated `map-variants.json` is read when a user selects one map variant.
  All other choices disappear and are persisted as absent; the user sees ordinary defaults with no
  recovery path.
- **Current control/evidence:** 64 KiB cap, async local gate, and temporary replacement protect
  ordinary reads/writes. No focused corruption-preservation test was located.
- **Residual risk:** This is silent local-state loss, distinct from harmless defaulting for a missing
  first-run file.
- **Disposition / owner:** **Mitigate; #315** (profile-aware preference recovery) with primitive work
  under **#270**.
- **Required deterministic test:** Seed invalid/oversize preferences plus a known-good sibling set;
  call `SetAsync`, then assert corrupt bytes are quarantined, existing valid preferences are recovered
  or the UI surfaces a decision, and no silent empty overwrite occurs.

### PERS-07 — raid history may be silently incomplete and exports can race queued writes

- **Severity:** Medium.
- **Source evidence:** The bounded outbox deliberately gives up after five failures and logs only once
  (`RaidHistoryOutbox.cs:205-249`); cancellation also returns without durable retry (`lines 222-230`).
  `ExportCsvAsync`/`ExportJsonAsync` delegate directly to `ListAsync` (`lines 99-123`) rather than
  first flushing, although `FlushAsync` exists and its comment identifies the race (`lines 125-149`).
- **Concrete scenario:** SQLite remains busy or disk I/O fails for one event. The write disappears,
  subsequent events continue, and a user can export/read before queued rows land. The report has a
  plausible but incomplete trail with no persisted gap marker or export warning.
- **Current control/evidence:** One reader preserves queued order; queue capacity is bounded; start is
  synchronous; `DisposeAsync` drains completed queue entries (`RaidHistoryOutbox.cs:21-28,41-71,151-172`).
  `RaidHistoryOutboxTests.cs` covers ordering and retry behavior, not a durable loss ledger or export
  barrier.
- **Residual risk:** Logging once is not user-visible history integrity. The data contract cannot
  distinguish “no observation” from “observation dropped after persistence failure.”
- **Disposition / owner:** **Mitigate; #270** (outbox/evidence stores). V1 source is intentionally
  lossy under persistent error; it is not a durable outbox implementation.
- **Required deterministic test:** Force exactly five persistent failures then a successful later event;
  assert a durable gap/failure record, visible incomplete status, ordered later data, and that export
  either flushes or reports bounded pending/lost entries deterministically.

### PERS-08 — raid history CSV permits spreadsheet formula interpretation

- **Severity:** Medium.
- **Source evidence:** CSV output quotes commas/quotes/newlines only (`SqliteRaidHistoryService.cs:333-356,396-402`).
  `outcome` and user-entered `notes` are exported verbatim; no cell neutralization is applied.
- **Concrete scenario:** A note beginning `=`, `+`, `-`, `@`, tab, or carriage return is stored locally,
  exported, then opened by a spreadsheet that evaluates it as a formula. CSV quoting does not prevent
  formula interpretation in common spreadsheet import paths.
- **Current control/evidence:** UTF-8 without BOM and RFC-style quote escaping are used. No formula
  injection test was found; named history tests contain ordinary persistence/trail cases, but no run
  was observed and no hostile export case was found.
- **Residual risk:** This can execute spreadsheet-side actions or exfiltration when a user shares/opens
  their own history. JSON export has a different consumer boundary and is not a CSV mitigation.
- **Disposition / owner:** **Mitigate; #315** (export/redaction) with storage evidence from **#270**.
- **Required deterministic test:** Persist each dangerous leading character in every string column;
  export CSV and assert cells are neutralized consistently while commas/quotes/newlines remain a single
  cell and data is visibly preserved.

### PERS-09 — raid history is not consistently context-scoped

- **Severity:** High.
- **Source evidence:** Writes include `profile_id` and a free-form `mode`
  (`SqliteRaidHistoryService.cs:22-61`), but `ListAsync` selects every raid without profile/mode
  predicate (`lines 314-330`), exports call that global list (`lines 333-364`), and map trails filter
  only map ID (`lines 155-238`). The schema has `raids.profile_id` but no generation/context field
  (`0001_initial.sql`, `raids` definition).
- **Concrete scenario:** Once a user imports/switches a profile or plays another mode, History or an
  export can reveal previous profile/mode raid notes, paths, and IDs under the current UI. A future
  profile system cannot reconstruct wipe generation for existing raid rows.
- **Current control/evidence:** Foreign keys preserve raid-to-profile reference and individual quest
  state is exact profile/mode/generation scoped (`0005_local_quest_progress.sql`; `SqliteQuestProgressStore.cs:15-71`).
  V1 ordinary history tests assume a single local profile; no multi-profile isolation test was located.
- **Residual risk:** This is current source behavior, even if the V1 UI ordinarily has one profile.
  It conflicts with the V2 context contract rather than being solved by it today.
- **Disposition / owner:** **Mitigate; #269** owns context semantics, **#270** owns schema/migration,
  and **#315** owns scoped history/export presentation.
- **Required deterministic test:** Seed same map across two profile IDs, all game modes, and two
  generations; switch active context rapidly, list/trail/export, and assert only exact active scope
  appears unless an explicit comparison mode is selected and labelled.

### PERS-10 — screenshot cleanup retains a check-then-act reparse/race exposure

- **Severity:** Medium.
- **Source evidence:** The cleanup enumerates `FileInfo`, checks attributes/time/name, then later
  hands `file.FullName` to recycle bin (`ScreenshotRetention.cs:89-125`). It skips an observed
  reparse-point attribute (`lines 146-149`) but does not bind recycling to that inspected object, and
  does not reject a reparse/junction screenshot root.
- **Concrete scenario:** Between metadata inspection and `Recycle`, a local process replaces the file
  with a symlink/reparse point or changes the root. The recycle operation acts on the later pathname,
  potentially a different user file. A cloud-only check does not close the time-of-check/time-of-use
  window.
- **Current control/evidence:** Filename allowlist, newest-file retention, recycle-bin-only operation,
  unavailable-bin no-op, and pre-check reparse exclusion are all real controls.
- **Residual risk:** V1 has no root ownership proof, stable-handle identity validation, last-run ledger,
  preview, or race/reparse test. The configuration default makes the exposure active by default.
- **Disposition / owner:** **Mitigate; #309.** #309 explicitly owns symlink/reparse, race, restore,
  default-off, and inspectability requirements; none is complete in V1.
- **Required deterministic test:** Use a controlled recycle-bin fake and synchronization hook to swap
  a candidate/root for a symlink/reparse point after enumeration; assert no recycle call for a changed
  identity/root and a user-visible failure ledger entry.

### PERS-11 — SQLite corruption and parse fallback behavior are inconsistent and not recoverable

- **Severity:** Medium.
- **Source evidence:** Several readers use throwing timestamp/Guid parsing (`SqliteRaidHistoryService.cs:378-391`,
  `SqliteTarkovDevResponseCache.cs:26-34`, `SqliteSyncStateRepository.cs:43-50,93-96`) while scan
  history silently maps corrupt timestamp, GUID, enum, confidence, or candidate JSON to defaults
  (`SqliteRecognitionRepositories.cs:115-169`). No connection-open `integrity_check`, corruption
  quarantine, or restore orchestration exists in `SqliteDatabase.cs:29-46` / migration runner.
- **Concrete scenario:** Power loss, manual edit, disk fault, or an old/future writer leaves one malformed
  row. History may throw and fail the whole page, cache/sync reads may fail a refresh path, while scan
  history silently reports `UnixEpoch`/`Unknown`, corrupting chronology without disclosure.
- **Current control/evidence:** WAL and transactions limit normal partial commits; scan view stays
  available for a bad individual row. Current tests cover normal timestamps and malformed position JSON,
  not SQLite corruption/row corruption policy.
- **Residual risk:** Silent invented defaults violate evidence honesty; throwing parsers turn a local
  defect into an opaque availability failure. No repair/backup selection is offered.
- **Disposition / owner:** **Mitigate; #270.** This is not satisfied by the future #270 corruption
  acceptance criterion.
- **Required deterministic test:** Inject malformed timestamp/GUID/enum/JSON rows and a corrupt SQLite
  fixture into every reader; assert one documented policy per store (quarantine with provenance or
  explicit recovery state), no invented timestamp/context, and a verified restore path for database
  corruption.

### PERS-12 — event definitions fail silently and writes lack cleanup/durability guarantees

- **Severity:** Low.
- **Source evidence:** Any malformed, oversized, unreadable, or unauthorized event file is skipped
  without logging (`JsonFileEventCatalog.cs:18-25,211-278`). Save writes `<path>.tmp`, moves it, and
  does not clean the temp in a `finally` or force a durable flush (`lines 115-148`); delete is direct
  (`lines 150-161`).
- **Concrete scenario:** A partial/denied hand-authored event file looks like an ordinary absent seasonal
  event; a failed write leaves a scratch file or a crash after rename loses durable acknowledgement.
  Duplicate IDs select the lexically first file, which can make an attacker/local editor shadow a real
  definition (`lines 230-248`).
- **Current control/evidence:** Per-file 256 KiB cap, direct-child enumeration, required-field checks,
  confidence cap, deterministic sorting, path-character replacement, and no data outside local files.
- **Residual risk:** The source calls silence intentional, but no diagnostic/recovery state distinguishes
  “out of season” from rejected local evidence. This is lower impact than profile/history data.
- **Disposition / owner:** **Defer; #270** storage/recovery primitives. It does not block the V1
  anti-cheat boundary but must not be treated as source-honest event state.
- **Required deterministic test:** Inject malformed/duplicate/permission-denied files and interrupted
  save/delete; assert a deterministic rejected-definition ledger, no temp residue, no duplicate shadow
  ambiguity, and existing valid definition retention.

## Cross-cutting observations and non-findings

- **Parameterization:** Persistence repositories pass values through `SqliteCommand` parameters in the
  reviewed write/read paths; migration SQL comes solely from embedded assembly resources. No SQL string
  interpolation from profile/import/history/settings content was found.
- **Cancellation:** Database and stream operations usually receive the caller token. It is not an
  integrity protocol by itself: cancellation during a transaction rolls back through disposal, but the
  outbox converts cancellation to a lost write (PERS-07), and file moves/metadata operations are not
  cancellable or tested at the boundary (PERS-05).
- **Timestamp semantics:** Quest exchange correctly rejects non-UTC serialized export time and normalizes
  writes. It intentionally does not claim export time is edit time (ADR 0006). The concern is corrupted
  read behavior and unscoped history, not a discovered systematic local-time writer.
- **Quest progress replay:** The exchange’s checksum plus exact scope/revision and import tables give
  stronger replay protection than `profile.json`. This finding does not claim that the quest exchange
  silently replays stale data; PERS-03 is explicitly about the different active-profile serializer.
- **No image persistence finding:** Reviewed ordinary scan/history tables hold metadata/geometry/JSON,
  not captured screen pixels. Screenshot retention acts on game-owned files and remains a separate,
  currently default-enabled cleanup feature.

## Reconciliation with the existing #317 baseline and V2 work

The existing Medium `RISK-LOCAL-IMPORT-EXPORT-INTEGRITY` row and TB-11 statement in
`docs/security/CONTROLS_AND_RESIDUAL_RISK.md` / `SYSTEM_AND_TRUST_BOUNDARIES.md` correctly keep the
broad bounded-document, replay, export-disclosure, and CSV boundary open. PERS-08 remains under that
umbrella. PERS-03 and PERS-09 are deliberately **not** folded into it: blind active-context
replacement and cross-context history disclosure can publish or expose the wrong profile, mode, or
generation, so they retain their audit's High severity as distinct
`RISK-PROFILE-IMPORT-STATE` and `RISK-CONTEXT-ISOLATION` entries. Those canonical rows name
#269/#270/#315 ownership, Mitigate dispositions, Reviewed source, and exact hostile-context tests.
PERS-05 maps separately to local file identity; PERS-01/02/11 add migration/corruption recovery;
PERS-04/10 add the local destructive-retention state; and PERS-06/12 cover other local JSON state.
This reconciliation neither downgrades a finding nor claims a test run that did not occur.

| Issue | Current V1 fact | Planned responsibility; not current evidence |
| --- | --- | --- |
| #269 | One JSON active profile; quest scope has profile/mode/generation, but history/profile switching is not atomic or consistently scoped. | First-class profile/game-mode/wipe/context behavior, deterministic legacy migration, atomic context publication, and incompatible-state quarantine. |
| #270 | WAL, transactions, a best-effort two-copy migration backup, and some bounded serializers exist. No full corruption, disk-full, lock, downgrade, rollback, restore, maintenance, or path-race contract exists. | Sole schema/migration/persistence owner: recoverable migrations, failure injection, durable outbox/evidence stores, context-aware data, retention/maintenance and explicit recovery APIs. |
| #309 | Screenshot cleanup is current V1, enabled by default, recycle-bin based, name/attribute filtered, with no preview/ledger/stable-handle race defense. | Default-off migration, exact preview, explicit consent, reparse/race/restore handling, Debug Capture separation and bounded recoverability. |
| #315 | V1 raid CSV/JSON export and simple map/layout preferences exist; exports are global history reads and CSV is formula-active. | Profile-aware preferences/layout recovery, scoped history/export, privacy redaction, draft/conflict recovery and future presentation. |

## Release posture

No Critical finding was established in this static lane. PERS-01, PERS-03, and PERS-09 are unresolved
High findings and therefore cannot be silently accepted for a V2 release; PERS-04 is also release-relevant
because it changes player-owned files after settings corruption. GitHub Actions remains the required
integration evidence: this documentation-only PR intentionally supplies no local build/test result, and
every required deterministic test above must be implemented in its owning workstream and cited by exact
head CI before changing a disposition.
