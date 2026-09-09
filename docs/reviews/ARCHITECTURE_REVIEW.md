# Architecture Review — Tarkov Companion (origin/main @ `20786ca`)

- **Reviewer:** Claude Code (independent adversarial review per handoff §36.5, Review 1)
- **Date:** 2026-09-09
- **Scope:** `origin/main` (`20786cad2a5246008be51fa7a3e53e094be3d6a8`) reviewed against `TARKOV_COMPANION_V1_AUTONOMOUS_BUILD_HANDOFF.md` and root `AGENTS.md`. Focus: architecture boundaries, actual runtime composition/wiring, whether the UI/simulator exercise implemented services, missing v1 acceptance gates, offline behavior, performance, and test quality.
- **Method:** static review of every project and test file, dependency-graph inspection, and exhaustive reference tracing (grep for construction sites of every service). No production code was modified. `dotnet build/test` was not re-run on this machine (workstation policy); CI (`.github/workflows/ci.yml`) builds and tests on push.

---

## Verdict: FAIL — not releasable as v1; not yet an integrated application

The repository contains a well-layered set of **libraries** — many individually competent, several genuinely good — and a **static UI mockup**, but **no application**. There is no composition root: of the 31 service interfaces in `src/TarkovCompanion.Core/Abstractions/ServiceContracts.cs`, **not one implementation is constructed anywhere in `src/` at runtime** (the sole exceptions are the migration runner inside a throwaway self-test database and `ScreenshotFilenameParser` inside the headless demo validator). The UI displays hardcoded fictional data; the simulator shares no code path with the companion; the smoke harness asserts only that processes stay alive and files exist; the scan, sync, raid, map, and recommendation pipelines are reachable only from the test projects.

To the project's credit, `docs/BUILD_STATUS.md:36-44` self-reports every post-foundation phase as unchecked and `docs/V1_RELEASE_REPORT.md:3` says "Status: In progress" — so this review is confirming an honest in-progress state, not exposing a false completion claim. But measured against the handoff's v1 acceptance checklist (§41), the build fails the majority of gates, and several artifacts (docs, test names, fixture matrices) **overstate** what has been proven. Details, severity-ranked, below.

Severity scale (handoff §36.6): **CRITICAL / HIGH / MEDIUM / LOW**, each with release-blocking status.

---

## CRITICAL findings

### C1. No composition root: the entire service layer is dead code at runtime

- **Area:** `src/TarkovCompanion.App/Program.cs`, `src/TarkovCompanion.App/App.axaml.cs`, all of `src/`
- **Concern:** The app's whole startup is `App.axaml.cs:13-25`: read a `--demo` flag and set `DataContext = MainWindowViewModel.CreateFoundationDemo(demoMode)`. There is no DI container, no service construction, no configuration, and no logging:
  - Grep across all of `src/` for `ServiceCollection`, `IServiceProvider`, `AddSingleton/AddScoped/AddTransient`, `IConfiguration`, `ILogger`, `IHttpClientFactory`: **zero hits** — despite `Microsoft.Extensions.DependencyInjection`, `.Logging`, and `.Configuration.Json` being declared dependencies (`src/TarkovCompanion.App/TarkovCompanion.App.csproj:19-22`; also `Infrastructure.csproj:8-10`, `Platform.Windows.csproj:10`). The handoff §5.1 lists these as required foundation; they are referenced but never imported by a single file.
  - Every one of the 31 interfaces in `ServiceContracts.cs` (lines 38–245) resolves to either an implementation constructed **only in `tests/`**, or (for `IRaidHistoryService`, `ServiceContracts.cs:227`) **no implementation at all**. The complete construction census: the App project constructs exactly three lower-stack types — `SqliteConnectionFactory`/`SqliteMigrationRunner` in `SelfTestRunner.cs:49-50` (against a temp DB deleted in `finally`, `SelfTestRunner.cs:42, 87`) and `ScreenshotFilenameParser` in `DemoRaidReplay.cs:105`.
  - The App project references `TarkovCompanion.Platform.Windows` (`TarkovCompanion.App.csproj:12`) but no `.cs` file in App imports its namespace. The simulator references Core and Application (`TarkovCompanion.EftSimulator.csproj`) and uses **zero** types from either.
- **Why it matters:** This is the difference between "v1 built" and "v1 not built." Recognition, data sync, raid state, maps, strategy, profile, and recommendations cannot execute in any shipped binary. Every downstream acceptance gate that says "works" (§41.3–§41.17) is structurally unmeetable until wiring exists. It also nullifies the handoff's runtime model (§11.1 startup, §15 scan flow).
- **Recommended correction:** Build the composition root first: in `Program.cs`/`App.axaml.cs`, resolve an app-data directory, run `SqliteMigrationRunner.ApplyAsync` on a persistent DB, construct `SqliteConnectionFactory → SqliteItemRepository`/`SqliteTarkovDevResponseCache → TarkovDevJsonClient → TarkovDevDataRefreshOperation → DataSyncService`, plus the profile service, recommendation stack, and (on Windows) platform services — via `Microsoft.Extensions.DependencyInjection`, with `ILogger` wired through. Everything below this review depends on it.
- **Release-blocking:** Yes.

### C2. The UI is a static mockup that fabricates evidence

- **Area:** `src/TarkovCompanion.App/ViewModels/MainWindowViewModel.cs`, `src/TarkovCompanion.App/Views/Pages/*.axaml`
- **Concern:** All 12 page ViewModels are parameterless records carrying three hardcoded strings (`MainWindowViewModel.cs:68-102`); all 12 page views contain **zero data bindings** (verified by grep: `Binding` count is 0 in every `Views/Pages/*.axaml`). Every displayed value is a literal:
  - `ItemsView.axaml` hardcodes "Graphics Card", "617,284 ₽ / slot", "Flea · 1,234,567 ₽ · 14m old", and quest advice text ("Keep found-in-raid units until Farming — Part 4 is complete").
  - `ScannerView.axaml:19` — "Scan visible item" button with no `Command` or `Click`; `:38-45, :99-101` hardcode "96% confidence", "1,234,567 ₽", "Salewa … 91% confidence".
  - `MainWindowViewModel.cs:184-190` fabricates a status strip: "Data · Synced · 18,462 records · 14m old", "Position · Train Yard · Screenshot · 12s old" — no sync, position, or scan ever occurred. `:147-153` fabricate a "last scan" ("96% confidence · OCR + icon agreement · 8s old").
  - `SettingsView.axaml:54` shows a confidence slider at "85%" bound to nothing; 85% matches no constant in the codebase (the real thresholds are 0.90/0.70/0.45 in `RecognitionPolicy.cs:15-19`).
- **Why it matters:** Handoff §9.5 requires ViewModels to invoke application services; §4.19/§30 forbid fabricating information the permitted inputs did not provide; §48 defines a v1 feature as "real". The demo mode required by §32.5 was supposed to "exercise major UI/data flows" via fixture replay — instead `--demo` flips ternaries to different hardcoded strings. A user (or a reviewer skimming screenshots) sees an evidence-annotated UI whose "evidence" is fiction — the exact anti-pattern the handoff's honesty rules exist to prevent.
- **Recommended correction:** Treat the current XAML as a design comp. Introduce per-page ViewModels that call the real services (search → `SqliteItemRepository.SearchAsync`, status strip → `RaidStateService`/`DataSyncService` state, scanner panel → recognition results), and make `--demo` drive those same ViewModels through the fixture replay path rather than a parallel string set.
- **Release-blocking:** Yes.

### C3. No real OCR engine exists; the recognition pipeline cannot run on real pixels

- **Area:** `src/TarkovCompanion.Infrastructure/Recognition/`
- **Concern:** The only `IOcrEngine` implementation in `src/` is `FixtureOcrEngine` (`FixtureOcrEngine.cs:8`) — a dictionary lookup keyed by the `CapturedImage.Source` **string** (`:27-30`) that reads zero pixels and returns pre-baked text from `fixtures/recognition/synthetic-scenes.json`. No Tesseract/Windows-OCR/other OCR package exists anywhere (`Directory.Packages.props` has none; repo-wide grep for tesseract/Windows.Media.Ocr: zero hits). Fatally, the real capture service tags images `"window:{ProcessName}"` or `"desktop-fallback"` (`GdiScreenCaptureService.cs:41, 57`), while fixture keys are `"fixture://…"` — so even if wired, every real capture would miss the dictionary and return zero OCR lines, and the pipeline would report `context_unknown` 100% of the time. Additionally:
  - `RecognitionService`/`IRecognitionService` are referenced by no consumer — the interface appears only at its declaration (`ServiceContracts.cs:113`) and implementation (`RecognitionService.cs:6`).
  - The icon-fallback path has no data source: `SkiaPerceptualIconMatcher` computes a genuine dHash but is constructed only in a test (`IconFingerprintTests.cs:14`); nothing downloads icons or builds fingerprints, and the `item_icon_fingerprints` table has zero code references.
  - `RecognitionService.cs:54-57` short-circuits `ExtractList` context with empty candidates instead of delegating to `ExtractRecognitionService` — the two are wholly disconnected even inside the subsystem.
- **Why it matters:** §4.2 (instant scanner) is the product's headline feature, and §5.4 explicitly requires an OCR abstraction *with a production backend* (Tesseract 5 or a Windows OCR). What exists is the abstraction plus test doubles. Acceptance §41.8 ("OCR path works", "icon fallback works") cannot pass.
- **Recommended correction:** Implement one production `IOcrEngine` (Tesseract 5 via a maintained .NET binding, or Windows.Media.Ocr behind the Windows platform project), key fixtures off request metadata rather than `Source` equality, add an icon-fingerprint builder fed from cached item icons, and route `ExtractList` context to `ExtractRecognitionService`.
- **Release-blocking:** Yes.

### C4. No runtime data path: the app never syncs, never persists, and has no offline behavior

- **Area:** `src/TarkovCompanion.Infrastructure/TarkovDevJson/`, `src/TarkovCompanion.Infrastructure/Persistence/`, `src/TarkovCompanion.App/`
- **Concern:** The sync/persistence chain (`TarkovDevJsonClient → TarkovDevDataRefreshOperation → SqliteDataRefreshRepository`, orchestrated by `DataSyncService`) is real and writes normalized tables transactionally — but it is invoked from exactly one place in the repository: `tests/…/SqliteDataPersistenceTests.cs:22`. The shipped app:
  - never constructs a persistent database — `SqliteDatabaseOptions` is instantiated once in `src/`, at `SelfTestRunner.cs:49`, pointing into `Path.GetTempPath()` and deleted after the self-test (`:42, :87`);
  - never calls `DataSyncService` or any repository;
  - implements none of the §11.1 startup sequence (open SQLite → migrate → load cached data → start UI → refresh stale in background). `docs/ARCHITECTURE.md:17` describes that sequence in the present tense; no code performs it.
  - The offline-capable HTTP cache is never connected: `SqliteTarkovDevResponseCache` is constructed only in one put/get test (`SqliteDataPersistenceTests.cs:137`); both client test helpers inject `InMemoryTarkovDevResponseCache`, so a stale-cache-across-restart path has never existed nor been exercised.
  - The smoke script's "offline relaunch" is vacuous: it sets `TARKOV_COMPANION_OFFLINE=1` (`scripts/windows-smoke.ps1:166`) — an environment variable **no source file reads** — and asserts a GUI process is still alive 2 seconds after launch (`:167-170`).
- **Why it matters:** §11 ("Do not make the app unusable because the network is down if a valid cache exists") and acceptance §41.2 ("Local cache works offline", "Data refresh does not block UI", "Data age is visible") are entirely unmet at the application level. Offline-first was a headline requirement, and currently there is no "online" either.
- **Recommended correction:** After C1's composition root exists: persistent DB under the platform app-data path, startup cache load + background staleness refresh, `SqliteTarkovDevResponseCache` wired into `TarkovDevJsonClient`, and a restart-offline integration test (populate cache → new process → no network → data served stale-marked).
- **Release-blocking:** Yes.

---

## HIGH findings

### H1. The Windows smoke harness proves liveness, not behavior — §33.5's required assertions are absent

- **Area:** `scripts/windows-smoke.ps1`, `.github/workflows/ci.yml`
- **Concern:** The handoff §33.5 requires the smoke run to trigger a scan via IPC and assert recognized item ID, map selection, parsed coordinates, extract set, container value, and offline cache (steps 7–19). The script instead asserts, per scenario (`windows-smoke.ps1:149-159`): simulator process alive, a state JSON echoes the scenario name it was launched with, a log file exists, a **zero-byte** screenshot marker exists, hardcoded safety flags are false, and the diagnostic channel `accepted` two echo commands. There are **zero** assertions about recognition, maps, position, extracts, containers, or cached data. It also never unpacks the release zip (takes `-AppPath`/`-SimulatorPath`, `:3-8`), and the self-test assertion `-not networkContacted` (`:119`) tests a hardcoded literal (`SelfTestRunner.cs:104`). The harness is not wired into CI, and per `dist/` (empty) has never been run against a packaged build.
- **Why it matters:** This is the gate that was supposed to substitute for live-EFT validation (§0, §33.5). As written it would go green against an application that recognizes nothing — which is in fact the current application.
- **Recommended correction:** After C1–C3: give the diagnostic channel a real scan command that returns the resolved item ID (see H2), then assert the §33.5 step list explicitly, starting from the unpacked `dist/` zip.
- **Release-blocking:** Yes (it is the release evidence mechanism).

### H2. The diagnostic channel's `Scan` command is a no-op acknowledgment

- **Area:** `src/TarkovCompanion.App/Services/Diagnostics/DiagnosticCommandChannel.cs`
- **Concern:** The channel supports two commands (`:6-10`); `Scan` returns the literal `"scan-requested"` and `Scenario` echoes the caller's string (`:47-49`). `DiagnosticCommandProcessor` (`:26`) has **no dependencies** — no capture service, recognizer, or ViewModel — so it cannot trigger anything. §33.5 step 8 requires triggering a companion scan through test IPC and asserting the resolved item ID; that is impossible against this implementation. (Credit: gating and hardening are genuinely good — developer-mode + ≥32-char token, constant-time compare, path-traversal-safe IDs, atomic temp-move responses, `:58-77, :111-121, :182-189`.)
- **Why it matters:** Combined with H1, every "diagnostic-scan-*" green check in a smoke report would attest to nothing.
- **Recommended correction:** Once a scan use-case exists, have `Scan` invoke it (capture → recognize → recommend) and return the recognition result payload; assert on it in the harness.
- **Release-blocking:** Yes.

### H3. The simulator does not simulate: one generic placard, no consumer for its outputs

- **Area:** `src/TarkovCompanion.EftSimulator/`
- **Concern:** All 10 scenarios render the identical shape composition — `BuildGenericArtwork` (`SimulatorWindow.cs:84-132`) draws the same rectangle/ellipse/bars regardless of scenario; only heading/detail strings and an accent color differ (`:52-68`). There is no item card, container grid, extract list, or flea rows for a recognizer to distinguish (§32.1/§32.3 scenarios exist in name only). `SimulatorFixtureEmitter` writes a 2-line log with frozen timestamps, a **zero-byte** screenshot marker, and a state JSON whose `IsGame`/`SendsInput` safety flags are hardcoded literals (`SimulatorFixtureEmitter.cs:37-70`). The simulator's log grammar (`"raid-start map=synthetic-harbor …"`, `SimulatorScenario.cs:20`) is parsed by **nothing** — `EftLogParser` expects a different invented grammar (`"raid_loading location='bigmap'"`), and no test or harness ever feeds simulator logs to the companion's parser. The simulator references Core/Application in its csproj and uses zero types from them.
- **Why it matters:** §32 makes the simulator "MANDATORY" as the mechanism that "allows the full integration pipeline to be proven." Nothing about the current simulator can prove any pipeline; it can only prove that a window opens and files appear.
- **Recommended correction:** Render per-scenario synthetic content the recognition pipeline can actually process (text at known regions for OCR, grid cells for the container scanner); emit logs in the same grammar `EftLogParser` consumes; write real (tiny) PNG screenshots; and add an integration test that runs simulator output through the companion's actual services.
- **Release-blocking:** Yes.

### H4. Raid history does not exist; ~24 schema tables are dead

- **Area:** `src/TarkovCompanion.Core/Abstractions/ServiceContracts.cs:227-240`, `src/TarkovCompanion.Infrastructure/Persistence/Migrations/0001_initial.sql`
- **Concern:** `IRaidHistoryService` has **no implementation** anywhere; CSV export appears nowhere in the codebase (grep hits only the `ExportCsvAsync` declaration). The migration creates the full §12 schema (53 objects — a genuine strength), but the following have zero code references beyond their `CREATE TABLE`: `raids`* , `raid_events`, `raid_positions`, `raid_extracts`, `scan_history`, `item_icon_fingerprints`, `key_intelligence_overrides`, `event_definitions`, `event_items`, `profile_event_item_state`, all seven `profile_*` tables, `player_profiles`, `app_meta`, `map_labels`, `map_render_configs`, `map_floor_layers` (*`raids` has one reference: a table-existence probe in `SelfTestRunner.cs:56`). Player profile data is instead persisted to a JSON file (`JsonFilePlayerProfileService.cs`) — a second, unreconciled persistence model for the same data the schema defines relationally. Curated key overrides are constructor-passed in memory (`KeyIntelligenceService.cs:34-36`), duplicating `key_intelligence_overrides`.
- **Why it matters:** §4.19/§41.16 (raid history, exports) fail outright. The dead tables also mislead: the self-test's "cache tables present" check green-lights a schema that mostly nothing uses. Two profile stores guarantee future divergence.
- **Recommended correction:** Implement `IRaidHistoryService` over the `raids`/`raid_*` tables and wire it to `RaidStateService` transitions; decide one profile persistence model (the JSON service is the better artifact — then delete the unused `profile_*` tables, or vice versa) and record the decision in an ADR (AGENTS.md rule 17).
- **Release-blocking:** Yes for §41.16; the dead-table cleanup itself is not.

### H5. Intelligence/recommendation services have no production data feed — profile context can never affect recommendations

- **Area:** `src/TarkovCompanion.Application/Services/Profile/ProfileNeedAggregationService.cs`, `Intelligence/*.cs`, `RecommendationEngine.cs`
- **Concern:** Every intelligence service takes its facts as constructor-supplied `IEnumerable`s with no loader: `ProfileNeedAggregationService(IEnumerable<QuestItemRequirement>, IEnumerable<HideoutItemRequirement>)` (`:26-48`) — and `QuestItemRequirement`/`HideoutItemRequirement` are constructed **only in unit tests**. The synced database *does* contain this data (`task_objective_items`, `hideout_requirements` written by `SqliteDataRefreshRepository.cs:351, 658`) but no reader exists — grep for those tables outside the refresh repository finds only their `DELETE FROM` statements. The same pattern holds for `AmmoIntelligenceService` (`:21-24`), `KeyIntelligenceService` (`:34-36`), `LoadoutIntelligenceService` (`:23-25`), and `StrategyModel`/`RoutePlanner` (no `RouteGraph` loader exists; `RouteGraph` is constructed once, in `StrategyServicesTests.cs:112`; `assets/strategy/*.json` is loaded by no code and copied to no output).
- **Why it matters:** §41.4 ("Quest progress affects recommendations", "Hideout progress affects recommendations", "Wishlist affects recommendations") and §41.7/§41.13/§41.14 are unmeetable: the bridge from synced data to the decision engines does not exist. The services are tested against hand-authored facts and can never see real ones.
- **Recommended correction:** Add repository-backed providers (SQLite → `QuestItemRequirement`/`HideoutItemRequirement`/`AmmoStats`/`KeyFacts`), an asset loader for strategy/route/curated files with provenance, and constructor-inject those in the composition root.
- **Release-blocking:** Yes.

### H6. The recognition "resolution/DPI/noise" test matrix is circular and pixel-free

- **Area:** `tests/TarkovCompanion.RecognitionTests/`, `fixtures/recognition/synthetic-scenes.json`
- **Concern:** The fixtures are JSON records of pre-baked OCR output (text + bounds + confidence), not images — `fixtures/recognition/` contains exactly one JSON file and the repo has no capture fixture images. The generated pixel buffers (`SyntheticFixtureLoader.cs:25-43`) are **never read by any code under test** (the fixture OCR engine is a dictionary; the context detector reads OCR strings, not pixels — `ScanContextDetector.cs:23-90`). Key assertions are self-referential:
  - `ContextAndOcrTests.cs:31` asserts the detector returns `expectedContext` — a field sitting in the same JSON three lines from the anchor tokens ("INSPECT", "EXTRACTS"…) that deterministically produce it.
  - `ContextAndOcrTests.cs:32` asserts `EstimatedUiScale ≈ fixture.Scale`, where the implementation is `median(line.Bounds.Height)/20` (`ScanContextDetector.cs:104-105`) and every fixture's line height is exactly `scale × 20` — i.e., `height/20 ≈ height/20`. The declared 1080p/1440p/4K resolutions influence nothing but an evidence string (`:81`).
  - The declared "noise" values are applied only to the never-read pixel buffer; the actual noise tested is hand-typed character corruption ("Gr@phics C@rd").
  - `docs/RECOGNITION.md:30` claims these fixtures "cover 1920×1080, 2560×1440, and 3840×2160 scenes at 100%, 125%, and 150% UI scale with deterministic synthetic noise" — metadata-only coverage presented as recognition-accuracy coverage.
- **Why it matters:** §33.3 and acceptance §41.8 ("1080p/1440p/4K recognition fixtures pass") intend rendered-image accuracy evidence. What passes today is a test of a dictionary and a substring table against their own inputs. The release report's future "accuracy by resolution" section (§44.8) has no honest data source.
- **Recommended correction:** Once a real OCR engine exists (C3), render actual synthetic scene images per resolution/scale/noise level and measure end-to-end accuracy; keep the current JSON fixtures as unit tests of the resolver/normalizer only, renamed accordingly.
- **Release-blocking:** Yes (as acceptance evidence; the underlying resolver/normalizer tests are fine as unit tests).

### H7. Systematic circular/tautological tests inflate apparent verification across the suite

- **Area:** `tests/` (all four projects)
- **Concern:** Beyond H6, a recurring pattern asserts either the fixture's own contents or the implementation's own literals:
  - `FullSyntheticRaidTests.cs:20-28` — 6 of 8 assertions echo `fixtures/simulator/full-raid.json` verbatim; the system under test (`DemoRaidReplay.RunAsync`) is a deserializer + field copier, and `report.EventTypes` is a projection of the fixture's own `type` fields whose ordering `ValidateFixture` already hard-requires (`DemoRaidReplay.cs:170-177`). `Assert.False(report.UsesLiveDetection)` tests the hardcoded `false` at `:142`.
  - `SelfTestIntegrationTests.cs:16-23` — asserts `NetworkContacted == false`, `Provider == "json.tarkov.dev"`, and all safety flags false: every one a hardcoded literal (`SelfTestRunner.cs:103-104, 116-119`). The self-test's own "search" check inserts a probe row and immediately FTS-matches it (`SelfTestRunner.cs:62-69, 127-135`) — it proves FTS5 is compiled in, not that any cache or data exists; the "provider" check is an unconditional pass (`:72`).
  - `PlatformContractTests.cs:9-21` — asserts a positional record stores its constructor argument. `Simulator/DiagnosticSafetyTests.cs:9-23` — asserts literals it constructed two lines earlier.
  - `MainWindowViewModelTests.cs:7-31, 61-69` — transcribes the hardcoded navigation and status-label arrays from `MainWindowViewModel.cs:114-125, 184-190`.
  - `RecommendationEngineTests.cs:20-27` — the found-in-raid override test cannot fail for the reason it names: the helper (`:58-67`) sets both `OutstandingQuestCount` and `OutstandingFoundInRaidQuestCount` from one parameter and both branches return `EssentialKeep`; only `Action` is asserted, never the reason code. Deleting the FIR branch keeps it green.
  - `SimulatorScenarioTests.cs:23-27` — asserts the scenario catalog equals a re-typed copy of itself (the same list is triplicated in `SimulatorScenario.cs`, the test, `windows-smoke.ps1:17-28`, and `docs/TESTING.md:38-47`).
  - The one test touching real native services self-disables on Windows: `WindowsIntegrationTests.cs:120-122` returns early if `OperatingSystem.IsWindows()`, so GDI/hotkey/DPAPI/monitor code paths have no assertions executed on **any** platform, ever.
  - Test-harness footnote: the hand-rolled `TestContext.CancellationToken` is always `CancellationToken.None` (`TarkovDevFixtureSupport.cs:95-103`), so the pervasive-looking cancellation plumbing in tests covers nothing.
  - Counterweight — genuinely good tests exist and should be kept as models: `ProfileIntelligenceTests.cs:18-108` (need arithmetic, suppression paths, sticky-allergy precedence), `ScreenshotFilenameParserTests.cs:28-47` (distinct rejection paths; exact quaternion→90° heading), `MapTransformTests.cs:9-44` (X/Z projection, flip, half-open floor bounds), `MapObservationServiceTests.cs:74-116` (refusal-to-guess invariants), `TextResolutionTests.cs:35-45` (threshold boundary theory), `SqliteDataPersistenceTests.cs:14-131` (real sync→persist→query round trip and transactional rollback), `SqliteMigrationTests.cs:40-90`, `DiagnosticChannelTests.cs:47-88`, `ContainerRecognitionTests.cs:13-28`, `IconFingerprintTests.cs:9-36`.
- **Why it matters:** Handoff §33 requires that "no major v1 subsystem is complete without tests or a deterministic simulator check" — the circular tests create the appearance of that completeness. A reviewer counting green checks (≈114 test cases) would radically overestimate verified behavior; the stale "25 tests" figure in `docs/BUILD_STATUS.md:32` / `V1_RELEASE_REPORT.md:24` compounds the confusion in the other direction.
- **Recommended correction:** Delete or rewrite the tautological tests (assert reason codes, not just actions; derive expectations independently of fixtures; never assert an implementation's own literals). Gate the native-service test to *run* on Windows CI instead of skipping there.
- **Release-blocking:** Yes in aggregate (test evidence is a release gate, §41.1/§41.19).

### H8. Release packaging gates unmet: `dist/` is empty, smoke never run, reviews not performed

- **Area:** `dist/`, `scripts/package-windows.sh`, `docs/V1_RELEASE_REPORT.md`, `docs/reviews/`
- **Concern:** `dist/` contains only `.gitkeep`; no `TarkovCompanion-v1.0.0-win-x64.zip`, no `SHA256SUMS.txt` (§40, §41.19). `package-windows.sh` is plausible but evidently never run in this tree. `windows-smoke.ps1` is not invoked by CI and has no recorded run. `docs/reviews/` did not exist before this report; none of the four required review passes (§36.5) had been produced. The release report is honestly "In progress" with all gates Pending (`V1_RELEASE_REPORT.md:25-30`) but contains stale/imprecise claims: "25 passed" tests (actual ≈114 cases across 93 methods), "10 projects" (12 sln entries incl. two solution folders), and a pre-written completion statement (`:58`) contradicted by its own Pending table.
- **Why it matters:** §41.19 enumerates these as hard gates; every one is currently red.
- **Recommended correction:** Run packaging + smoke only after C1–C3/H1–H3 make them meaningful; refresh the report's test counts from CI output rather than hand-maintained numbers.
- **Release-blocking:** Yes.

---

## MEDIUM findings

### M1. Dependency-direction drift: Infrastructure and Platform.Windows reference Application

- **Area:** `src/TarkovCompanion.Infrastructure/TarkovCompanion.Infrastructure.csproj:5`, `src/TarkovCompanion.Platform.Windows/TarkovCompanion.Platform.Windows.csproj:7`
- **Concern:** The handoff's structure (§8, §9) puts contracts in Core with Application, Infrastructure, and Platform as siblings. Here both outer layers additionally depend on the Application assembly (for `FuzzyMatcher`, `TextNormalizer`, `ScreenshotFilenameParser` and friends). `docs/ARCHITECTURE.md:5-11` documents the arrow, so it is a conscious choice — but it means "utility" code in Application is now load-bearing for two other layers, and Application can never reference an Infrastructure type without a cycle (which is partly why fixture/loader gaps in H5 are awkward to fix cleanly).
- **Why it matters:** Layer drift compounds: text-processing shared kernel belongs in Core (it has no I/O), which would restore sibling independence.
- **Recommended correction:** Move `TextNormalizer`/`FuzzyMatcher` (pure functions) to `Core/Common`; re-evaluate whether the remaining Application references are needed. Record in an ADR either way.
- **Release-blocking:** No.

### M2. RecommendationEngine deviates from the specified priority model and under-delivers explanations

- **Area:** `src/TarkovCompanion.Application/Services/RecommendationEngine.cs:47-129`
- **Concern:** Against §13.2's ladder (allergy → event → quest-FIR → hideout → wishlist → key/ammo → economic → drop): the event rule ranks **below** hideout and wishlist (`:95` vs `:83, :89`) and only fires for Provision/Medicine categories; key/ammo is not a decision tier at all — specialized advice only appends a reason (`:101-107`) and can never yield `Keep`; `DropFirst` is evaluated **before** the sell comparisons (`:115` vs `:121, :127`). `RecommendationAction.Use` is never returned by the engine (its only producer is on a method not exposed by `IEventTrackerService` — see M8). Reason codes `HighValuePerSlot` and `AmbiguousRecognition` (`Recommendations.cs:33, 36`) are never emitted. Confidence is a pass-through of caller-supplied context (capped 0.60 without price, `:27-29`) rather than derived; `DataProvenance.Age()` (`DomainPrimitives.cs:36`) is dead code, so staleness never affects output. Every branch returns after a single reason, so the "ordered reason codes" requirement (§13.1) degenerates to one code per recommendation.
- **Why it matters:** The engine is "the central value layer" (§13). The deviations are individually defensible but none is documented in an ADR, and §41.3's "explainable" bar is weakly met with single-reason output.
- **Recommended correction:** Either implement §13.2's ordering (and multi-reason accumulation) or document the deviation in an ADR; emit the dead reason codes or delete them; wire staleness into confidence.
- **Release-blocking:** No (quality/spec-fidelity).

### M3. HTTP client: four correctness defects inside otherwise excellent resilience code

- **Area:** `src/TarkovCompanion.Infrastructure/TarkovDevJson/TarkovDevJsonClient.cs`
- **Concern:** The client genuinely implements bounded timeouts, cancellation, capped exponential backoff with jitter, request dedup, ETag/If-Modified-Since with 304 handling, stale-while-revalidate, and offline fallback (`:230-343, :353-411` — the strongest file in the repo). Defects:
  1. Dedup leaks the first caller's `CancellationToken` into the shared `Lazy` task (`:256-260`); a second caller receives a foreign `OperationCanceledException`, which the stale-fallback filter deliberately excludes (`:245-248`) — so caller B gets a cancellation it never requested instead of its stale cache.
  2. `force: true` silently joins an already-running non-forced background refresh occupying the `_inFlight` slot (`:237, :243`), dropping the forced semantics and the second caller's validators.
  3. Background SWR refresh is fire-and-forget with `CancellationToken.None` and swallowed errors (`:237, :389-392`) — unsupervised at shutdown, invisible without logging (there is none, C1).
  4. Retry delay uses the injected `TimeProvider` (`:380`); tests only avoid hangs by zeroing `InitialRetryDelay` (`TarkovDevJsonClientTests.cs:211`), so the backoff path is untested with a fake clock.
- **Why it matters:** These are the bugs that surface as rare production mysteries precisely when offline resilience matters.
- **Recommended correction:** Dedup with a token-independent task + per-caller linked cancellation; force-bypass or replace the in-flight entry; track background refreshes for drain-on-dispose; add a fake-clock backoff test.
- **Release-blocking:** No.

### M4. Search performance: unbounded fuzzy scan defeats FTS5; §34 targets unmeasured

- **Area:** `src/TarkovCompanion.Infrastructure/Persistence/Repositories/SqliteItemRepository.cs:142-213`
- **Concern:** `SearchAsync` runs three passes; pass 3 is `SELECT id, name, short_name FROM items;` — no WHERE, no LIMIT — then computes two `FuzzyMatcher.Similarity` calls per row, each of which re-normalizes **both** strings (StringBuilder + regex per call, `FuzzyMatcher.cs:5-30`, `TextNormalizer.cs:16-29`), with Damerau-Levenshtein matrices for near misses, followed by a per-candidate `GetAsync` N+1 (`:42-49`). With a realistic ~20k-item catalog that is ~80k normalizations + regex passes per keystroke-triggered search. Meanwhile genuine FTS5 results are assigned a hardcoded score `0.9` (`:188`) — `bm25` is ordered by, then discarded — so FTS relevance never influences ranking. No performance measurement exists anywhere (§34 requires ≤50 ms p95 search, ≤3 s cold start, ≤400 MB, all "recorded in `docs/V1_RELEASE_REPORT.md`" — that section is empty, and with 2-item fixtures nothing could have measured it). Secondary: refresh writes are one `SqliteCommand` per row (`SqliteDataRefreshRepository.cs:669-685`, ~100k command constructions per items refresh), and `SqliteCacheMode.Shared` is combined with WAL (`SqliteDatabase.cs:16, 38`), a counter-indicated pairing.
- **Why it matters:** The only performance-relevant code path in the repo has an O(catalog) scan with heavy per-row allocation on its hot path; §41.3 gates and §34 targets are unverifiable.
- **Recommended correction:** Cache normalized names (columns exist: `normalized_name`, `normalized_short_name`) and normalize the query once; gate pass 3 behind "passes 1–2 returned < limit" and cap it; use bm25 for scoring; batch the hydration query; add a benchmark against a realistic-size fixture before claiming §34 numbers.
- **Release-blocking:** No (latent until wired), but blocks the §34 evidence gate.

### M5. Map refresh cascade-destroys render configs and floor layers

- **Area:** `src/TarkovCompanion.Infrastructure/Persistence/Migrations/0001_initial.sql:157-176`, `SqliteDataRefreshRepository.cs:190`
- **Concern:** `map_render_configs` (holding `cached_asset_path`, attribution, transforms) and `map_floor_layers` are `REFERENCES maps(id) ON DELETE CASCADE`; `RefreshMapsAsync` begins with `DELETE FROM maps;`. Every map sync would wipe the render/transform/attribution cache — the §25.1 data these tables exist for. Latent today only because nothing writes those tables (H4); a first integration would hit it immediately. Related: `MapCacheJsonParser` documents reading "the app's normalized cached map document" (`MapCacheJsonParser.cs:7`) that nothing writes, while `RefreshMapsAsync` writes SQLite tables nothing reads — two disconnected map caches.
- **Recommended correction:** Upsert maps instead of delete-all, or exclude locally-derived tables from the cascade; unify the two map cache models.
- **Release-blocking:** No (latent).

### M6. Raid/log subsystem: invented log grammar, unreachable states, mislabeled evidence

- **Area:** `src/TarkovCompanion.Application/Services/Raids/EftLogParser.cs`, `RaidStateService.cs`, `fixtures/logs/raid-session.log.fixture`
- **Concern:** `RaidStateService` is a genuine, careful state machine (monotonic-evidence guard, per-raid GUIDs, manual-override persistence, simulator gating — `:26-124`). But: the log grammar is invented, and the 5-line fixture was authored to hit the parser's own substring markers (`raid_loading location='bigmap'`) — no captured real EFT log sample exists in the repo, so §41.11 "fake log selects expected map" is proven only against a format the parser's author made up (the map-alias table `bigmap→customs` etc. at `:13-33` *is* researched; the line format is not). `RaidLifecycleState.LauncherOrGameDetected` and `RaidEvidenceKind.ProcessDetected` are declared but unproducible (only references are their declarations, `Raids.cs:9, 18`), so §23.3's state machine is missing a required state in practice. `ApplyExtracts` tags OCR observations as `RaidEvidenceKind.ManualOverride` (`RaidStateService.cs:108`) — works today by accident of a null-map condition (`:42`), and will mislabel evidence provenance the moment anyone reads it.
- **Recommended correction:** Capture at least one sanitized real log excerpt as a fixture (§23.2 "fixture-driven" intends real formats); implement or remove the unreachable enum members; add an `OcrObservation` evidence kind.
- **Release-blocking:** No, with the caveat that live-EFT map detection risk is currently unbounded and correctly flagged in `docs/LIVE_EFT_VALIDATION.md`.

### M7. Strategy/route engines are real algorithms with no data and cosmetic disclaimers-only tests

- **Area:** `src/TarkovCompanion.Application/Services/Strategy/`, `assets/`
- **Concern:** `RoutePlanner` is a correct Dijkstra with honest degradation (`RoutePlanner.cs:20-116` — the best code in the Application layer), and `StrategyModel` implements the §27.2 decay/attraction envelopes (`StrategyModel.cs:56-73`). But no production data source exists: `RouteGraph` and `StrategyZone` are constructed only in tests; `assets/strategy/generic-training-ground.json` (3 zones, 730 bytes), `assets/curated/key-overrides.example.json` (1 disabled placeholder), and `assets/events/allergy-style.example.json` (1 inactive template) are loaded by no code and copied to no output. Several strategy tests assert the implementation's own literal strings/constants (`StrategyServicesTests.cs:73-81` vs `RoutePlanner.cs:64, 71, 78`). Per-map curated strategy for real maps (§27.6 `assets/strategy/customs.json`) does not exist.
- **Recommended correction:** Asset loaders with schema validation + provenance, at least one real-map strategy/graph file, and behavior-derived test expectations.
- **Release-blocking:** No for the algorithms; §41.13/§41.14 gates remain red until data exists.

### M8. Contract/implementation mismatches leave shipped features unreachable through interfaces

- **Area:** `src/TarkovCompanion.Core/Abstractions/ServiceContracts.cs`, `src/TarkovCompanion.Application/Services/Profile/ProfileEventTrackerService.cs`
- **Concern:** `ProfileEventTrackerService`'s consumption recording, decision, and counts APIs (`RecordConsumptionAsync` `:101`, `GetConsumptionDecisionAsync` `:138`, `GetStateCountsAsync` `:86`) are not on `IEventTrackerService` (`ServiceContracts.cs:92-99`) — the entire allergy test/record workflow (§21, §41.5) is invisible to any interface-typed consumer, and its `Use` recommendation (`:148`) is the only producer of that action (M2). Five implementations are fully dead even in tests: `ItemSearchService`, `MapDataService`, `InMemoryMapDefinitionCache`, `ProfileQuestProgressService`, `ProfileHideoutProgressService` (zero constructions anywhere). Core hardcodes the auto-select threshold `0.90` in `RecognitionResult.Selected` (`Recognition.cs:52`) duplicating `RecognitionPolicy.AutoSelectThreshold` (`RecognitionPolicy.cs:15`) with nothing testing their agreement.
- **Recommended correction:** Widen `IEventTrackerService` (or add a second interface); delete or wire the dead services; single-source the threshold.
- **Release-blocking:** No.

### M9. Platform-layer edge defects (in otherwise competent, safety-clean P/Invoke)

- **Area:** `src/TarkovCompanion.Platform.Windows/`
- **Concern:** All eight platform services are real implementations with correct OS gating and no prohibited APIs (verified: no memory read/write, hooks, SendInput, packet capture anywhere in the repo — the §3 safety boundary holds). Defects: (a) `WindowsScreenshotWatcher`'s simulator self-exclusion filters on the *path* containing `"EftSimulator"` (`:42`), but emitted screenshot filenames (`SimulatorFixtureEmitter.cs:51`) never contain that string — the guard only works if the caller happens to choose such a directory, contradicting `docs/WINDOWS.md:21`; (b) `GdiScreenCaptureService` will capture the **entire desktop** as fallback gated only by caller-supplied `AllowDesktopFallback` (`:24-58`) — with no caller in existence, the privacy-relevant default is an unmade decision that must be made deliberately (default false); (c) `SimulatorFixtureEmitter` uses `FileMode.CreateNew` (`:53`) and throws on any re-run against a fixed screenshot root.
- **Recommended correction:** Match simulator exclusion on window/process identity, not path substrings; default desktop fallback to false and require explicit settings opt-in; use `FileMode.Create` or unique names.
- **Release-blocking:** No.

### M10. Documentation asserts an unbuilt runtime in the present tense

- **Area:** `docs/ARCHITECTURE.md`, `docs/DATABASE.md`, `docs/RECOGNITION.md`, `docs/TESTING.md`
- **Concern:** `ARCHITECTURE.md:15-19` describes startup cache load, background refresh, and the full scan pipeline as existing behavior — none of it is wired (C1/C4). `DATABASE.md:9` prescribes a startup sequence no shipped code performs. `RECOGNITION.md:30` presents metadata-only fixtures as resolution coverage (H6). `TESTING.md:104` says the smoke harness "relaunches the demo with offline mode set" — the env var is read by nothing (C4). Meanwhile `BUILD_STATUS.md` and `V1_RELEASE_REPORT.md` are commendably honest about phase status but carry stale numbers (H8). Handoff §44.17 requires the release narrative to "accurately identify what was simulated vs actually verified" — current docs blur designed-vs-built.
- **Recommended correction:** Rewrite the aspirational sections in future tense or move them to a design doc; regenerate counts from CI.
- **Release-blocking:** Yes for the release-report accuracy gate; No for the rest.

---

## LOW findings

### L1. Dead dependencies and hand-rolled substitutes

`Microsoft.Extensions.DependencyInjection`/`.Logging`/`.Configuration.Json`/`.Http` and `CommunityToolkit.Mvvm` are referenced (`App.csproj:19-22`, `Infrastructure.csproj:8-10`, `Platform.Windows.csproj:10`) with zero usages; `MainWindowViewModel.cs:7-35` hand-rolls `INotifyPropertyChanged`/`ICommand` beside the unused toolkit. Remove or use them (C1 will use three of the five).

### L2. Script gaps

`scripts/audit-safety.sh:7-10` scans only `src/` and root props/csproj — not `tests/`, `scripts/`, or `fixtures/` — and its root-csproj glob matches nothing (silently swallowed by `2>/dev/null`); it requires `rg` without a check. `scripts/orchestrate-v1.sh` is a 3-line `printf` stub listed beside real build scripts. `package-windows.sh` assumes `zip`/`sha256sum` without checks.

### L3. Assorted latent defects

- `sync_state.content_hash` is written (`SqliteSyncStateRepository.cs:63, 72`) but never read — change detection scaffolded, not implemented; ETag/Last-Modified validators are duplicated across `sync_state` and `http_response_cache` with only the latter consulted.
- `item_search.aliases` FTS column is always inserted empty (`SqliteDataRefreshRepository.cs:159`).
- `KeyIntelligenceService.cs:96-97` populates `RemainingUses` with `MaximumUses` — no usage tracking exists, so the field is misleading.
- `ProfileNeedAggregationService.cs:41-47` reports hideout validation failures under `nameof(questRequirements)`.
- `MapTransformService.SelectFloor` (`:39-40`) is input-order-dependent (`FirstOrDefault`, no sort) while `MapTransformValidator` sorts before validating — the two disagree on whether order matters.
- Dead diagnostic surface: `CoordinatedOcrResult.FullFrame`, `ContextDetection.Confidence/.Evidence` are computed and never read; `SelfTestRunner.cs:119` hardcodes `diagnosticChannelEnabled=false` regardless of actual state.
- Diagnostic channel validates scenario IDs before authenticating the token (`DiagnosticCommandChannel.cs:34, 40`), letting unauthenticated callers distinguish malformed IDs from bad tokens (dev-only channel; cosmetic).

---

## What is genuinely good (keep and build on)

1. **The safety boundary holds.** Independently verified: no process-memory, injection, hooking, input-synthesis, or packet-capture APIs anywhere; P/Invoke inventory is limited to GDI capture, window/monitor enumeration, `RegisterHotKey`, DPI, and DPAPI, all OS-gated. §3/§41.18 gates pass.
2. **`TarkovDevJsonClient`** — full resilience feature set (timeouts, backoff+jitter capped at 3 attempts, dedup, ETag/304, SWR, offline fallback), well-tested at the class level (M3 defects notwithstanding).
3. **Schema and migrations** — the full §12 model exists with embedded, transactional, ordinal migrations and a real upgrade test (`SqliteMigrationTests.cs:40-90`); FTS5 is in place.
4. **`SqliteDataRefreshRepository`** — transactional normalized refresh with tombstone deletes and a genuine rollback-on-invalid-snapshot test.
5. **`JsonFilePlayerProfileService`** — atomic writes, size caps both directions, schema versioning, deterministic normalization, secret-free export with a test asserting it (`ProfilePersistenceTests.cs:27`).
6. **`RoutePlanner` / `RaidStateService` / `MapObservationService`** — real algorithms with honest degradation ("no route is guessed"; refusal to plot without a validated transform), matching the handoff's honesty requirements.
7. **Honest status docs** — `BUILD_STATUS.md` phase checkboxes and `V1_RELEASE_REPORT.md` "Pending" gates accurately reflect the in-progress state.
8. **CI exists and is sane** — Linux build/test + safety/secret audits + Windows publish (`.github/workflows/ci.yml`).

---

## V1 acceptance-gate status (handoff §41)

| Area | Status | Blocking evidence |
|---|---|---|
| 41.1 Build/platform | **Partial** | Builds + CI green claimed; Linux demo is a mockup (C2); no VM smoke run; package absent (H8) |
| 41.2 Data | **Fail** | Sync works only in tests (C4); no offline runtime path; data age fabricated in UI (C2) |
| 41.3 Item economy | **Fail** | Search never reachable (C1); recommendation unreachable; ₽/slot only in domain tests |
| 41.4 Profile context | **Fail** | Profile service unwired; no data bridge to recommendations (H5) |
| 41.5 Event/allergy | **Fail** | Logic exists and is well-tested; unreachable via interface and runtime (M8, C1) |
| 41.6 Ammo / 41.7 Keys | **Fail** | Services exist, constructor-fed only (H5); no browser UI (C2) |
| 41.8 Recognition | **Fail** | No OCR engine (C3); fixture matrix circular (H6) |
| 41.9 Container | **Fail** | Real segmenter, never composed; simulator scene unrecognizable (H3) |
| 41.10 Flea | **Fail** | Parser exists in isolation; no automation code exists (pass on the safety half) |
| 41.11 Raid/map | **Fail** | State machine/transforms real and tested; no runtime wiring; invented log format (M6) |
| 41.12 Extracts | **Fail** | Service exists; disconnected from RecognitionService (C3) and runtime |
| 41.13 Strategy / 41.14 Routes | **Fail** | Real algorithms, no data source (M7); disclaimers present (pass on labeling) |
| 41.15 Loadout | **Fail** | Service exists, unwired |
| 41.16 Raid history | **Fail** | No implementation at all (H4) |
| 41.17 Windows integration | **Fail** | All services exist; none constructed; native paths asserted on no platform (H7) |
| 41.18 Safety/licensing/privacy | **Pass** (code) / Partial (process) | No prohibited code (verified); license docs exist; dependency audit in CI; final inventory pending |
| 41.19 Release | **Fail** | No package, no smoke report, reviews only now beginning (H8) |

---

## Recommended path (priority order)

1. **C1** — composition root + DI + logging + persistent app-data DB. Everything else is blocked on this.
2. **C3** — one real OCR engine + fixture keying fix + icon fingerprint source; connect `ExtractRecognitionService`.
3. **C4/H5** — startup sync/offline flow; repository-backed providers feeding the intelligence services.
4. **C2** — page ViewModels bound to real services; make `--demo` replay through the same code path.
5. **H2/H3/H1** — real scan IPC command; simulator scenes the pipeline can process; rewrite smoke assertions to §33.5.
6. **H4** — implement `IRaidHistoryService`; resolve the dual profile-persistence model via ADR.
7. **H7/H6** — purge circular tests; convert the fixture matrix into honest accuracy measurement.
8. **M-series** as encountered during the above (M3 and M5 before first production sync; M4 before claiming §34 numbers).

---

*Review method note: every finding above cites file/line evidence gathered from this worktree at commit `20786ca`. Claims about absence (no construction sites, no OCR engine, no env-var readers, dead tables) were established by exhaustive repo-wide reference searches. Four parallel read-only audit passes (data/persistence, recognition, simulator/platform/scripts, application-services/tests) fed this synthesis; load-bearing citations were independently spot-verified.*
