# Recognition subsystem review

- **Reviewer:** Claude Code (independent adversarial review, per handoff §36.5 Review 2)
- **Date:** 2026-09-09
- **Scope reviewed:** `origin/main` recognition implementation (`src/TarkovCompanion.Infrastructure/Recognition/`, `tests/TarkovCompanion.RecognitionTests/`, `fixtures/recognition/`, related contracts, capture, simulator, and docs). Reviewed at worktree commit `20786ca`; the only later `origin/main` commit (`eb12c9f`) touches one integration test and does not change recognition.
- **Method:** static source review against `TARKOV_COMPANION_V1_AUTONOMOUS_BUILD_HANDOFF.md` (§5.4, §15, §16, §17.2, §26, §33.3, §34, §35.1, §41.8/41.9/41.10/41.12) and root `AGENTS.md`. No fixes were implemented. Tests were not executed as part of this review (workstation policy); CI runs them.

## Summary verdict

The recognition code that exists is cleanly layered, deterministic, honest about thresholds at the arithmetic level, and safe (no capture persistence, no game input, no cloud upload). However, it is a **library of unassembled parts, not a working scanner**: there is no production OCR engine, no caller anywhere in the application wires capture → recognition → UI, the icon fallback has no reference data source and hashes the wrong image, and the fixture suite is circular in a way that makes any future "accuracy by resolution" claim vacuous if populated from it. Several acceptance items in handoff §41.8–41.12 are currently unreachable, and one behavior (closed extracts marked active) is a codified false-state bug with a test asserting it. `docs/BUILD_STATUS.md` honestly leaves "Recognition" unchecked and the release report is guarded, so no dishonest claim has been *published* yet — this review's job is to keep it that way.

Findings are severity-ranked. "Release-blocking" means blocking for a v1 tag per handoff §41.

---

## CRITICAL

### C1. No production OCR engine exists — the OCR path cannot run outside tests

- **File/area:** `src/TarkovCompanion.Infrastructure/Recognition/FixtureOcrEngine.cs`; `src/TarkovCompanion.Core/Abstractions/ServiceContracts.cs:118` (`IOcrEngine`)
- **Concern:** The only `IOcrEngine` implementations in the repository are `FixtureOcrEngine` (returns scripted lines keyed by `image.Source` string, ignoring pixels entirely — `FixtureOcrEngine.cs:27`) and a private `RecordingOcrEngine` test double (`ContextAndOcrTests.cs:106`). Handoff §5.4 requires a real offline OCR provider (Tesseract 5 preferred, Windows-specific backend acceptable) with the fixture engine existing *in addition* for deterministic Linux tests. `docs/RECOGNITION.md:7` says "production OCR providers implement the existing Core `IOcrEngine` contract" — none does.
- **Why it matters:** Every downstream capability (single-item scan, extract OCR, container OCR, flea parsing) is inert in the shipped app. Acceptance items 41.8 "OCR path works", 41.12 "Extract OCR works in simulator", and the Windows smoke assertion "assert expected item ID/result" (handoff §33.5 step 9) are unachievable. Worse, if the fixture engine were ever wired in as a stopgap, results would be fabricated.
- **Remediation:**
  1. Implement `TesseractOcrEngine` in Infrastructure (Tesseract 5 via a maintained .NET binding — license-review it first per AGENTS.md rule 12/14, and vendor the `eng` traineddata with provenance) and/or a `WindowsMediaOcrEngine` in `Platform.Windows` behind the same interface.
  2. Select the provider via DI/configuration; report availability through the self-test (`--self-test` must include "OCR provider availability" per handoff §33.6).
  3. Keep `FixtureOcrEngine` test-only (move it to the test project or mark it clearly; nothing in production should be able to construct it).
- **Release-blocking:** Yes.

### C2. The recognition pipeline has zero production callers — scan hotkey → capture → recognize → UI does not exist

- **File/area:** whole `src/TarkovCompanion.Infrastructure/Recognition/`; `src/TarkovCompanion.App/App.axaml.cs:20`; `src/TarkovCompanion.App/ViewModels/MainWindowViewModel.cs:71–153`; `src/TarkovCompanion.App/Services/Diagnostics/DiagnosticCommandChannel.cs:47`
- **Concern:** Grep-verified: no type outside `Infrastructure/Recognition` and the test projects references `RecognitionService`, `OcrCoordinator`, `FuzzyCanonicalItemResolver`, `ExtractRecognitionService`, `ContainerGridSegmenter`, `ContainerScanAnalyzer`, `FleaListingParser`, or `SkiaPerceptualIconMatcher` (the sole exception is the `IExtractRecognitionService` interface declaration itself). There is no DI composition root at all; the App boots `MainWindowViewModel.CreateFoundationDemo(...)` with hardcoded strings ("Confidence · 96% · observed 8 seconds ago", "Graphics Card", "96% confidence · OCR + icon agreement"). The diagnostic `Scan` command returns a static `"scan-requested"` acknowledgment without invoking recognition. The `DemoRaidReplay` "scan" event just copies an `itemId` field from fixture JSON. Nothing builds the `CanonicalItemReference` catalog from the items database, and nothing writes `scan_history` (schema exists at `Persistence/Migrations/0001_initial.sql:187` but has no repository).
- **Why it matters:** The universal scan flow of handoff §15.1 — context inference dispatching to item / extract / container / flea handling — is the core product feature (§2, §4.2) and is entirely absent. `RecognitionService` itself punts on two of its four contexts (returns empty results with `extract_context` diagnostic for extract lists, and never invokes the container analyzer for containers), on the assumption that an orchestrator will dispatch — but no orchestrator exists. Acceptance 41.8 "External single-item scan works in simulator" cannot pass; the Windows smoke test cannot assert a resolved item.
- **Remediation:**
  1. Add an Application-layer `ScanUseCase` (or `IScanService`): `IScreenCaptureService.CaptureAsync` → `IRecognitionService.RecognizeAsync` → dispatch by detected context to `IExtractRecognitionService` (with current map from raid state), a new container-scan orchestrator, and the flea parser → feed `IRecommendationEngine` → publish to the UI via an observable scan-state service.
  2. Build the resolver catalog (names, short names, aliases) from `IItemRepository` at startup/sync and inject it.
  3. Create a real composition root (`Microsoft.Extensions.DependencyInjection` per handoff §5.1) registering the recognition graph, with the Windows capture/hotkey providers on Windows and fixture equivalents in demo mode.
  4. Extend `DiagnosticCommandChannel` so `Scan` executes the use case and the response carries the resolved canonical id + confidence, making smoke step 9 assertable.
  5. Persist scan results to `scan_history` (without pixel data) so raid-history acceptance (41.16 "Scan events recorded") becomes reachable.
- **Release-blocking:** Yes.

### C3. Fixture suite is circular — it can never substantiate resolution/scale/noise accuracy claims

- **File/area:** `fixtures/recognition/synthetic-scenes.json`; `tests/TarkovCompanion.RecognitionTests/SyntheticFixtureLoader.cs:25–43`; `docs/RECOGNITION.md:30`; `docs/V1_RELEASE_REPORT.md:36–38`
- **Concern:** `FixtureOcrEngine` returns the scene's scripted lines keyed on the `source` string — pixels, resolution, UI scale, and the `noise` field have **no effect on OCR output**. `SyntheticScene.CreateImage()` fabricates a gray buffer with a sparse fill pattern that nothing reads (context detection and resolution operate purely on the scripted text; only the unused-in-pipeline icon hash would ever look at pixels). The "noise" is hand-typed character confusions (`Gr@phics C@rd`, `0BJECTlVES`). The suite is 5 scenes × 3 lines = 15 OCR lines with exactly **one** resolvable item name in total, and the resolution/scale coverage is one diagonal (1080p/100%, 1440p/125%, 4K/150% + two extras), not the matrix handoff §33.3 describes. `docs/RECOGNITION.md:30` ("covers 1920×1080, 2560×1440, and 3840×2160 scenes at 100%, 125%, and 150% UI scale with deterministic synthetic noise… exercise every supported context") reads far stronger than what exists.
- **Why it matters:** These fixtures test the resolver/normalizer/policy math (legitimately useful) but prove nothing about OCR robustness to resolution, scaling, or noise — the thing acceptance 41.8 ("1080p/1440p/4K recognition fixtures pass", "scaling/noise fixtures pass at acceptable accuracy") and release-report requirement §44.8 ("recognition fixture accuracy by resolution/context") actually mean. If the release report is ever populated from this suite, it will be a false-accuracy claim of exactly the kind AGENTS.md rule 19 and handoff §48 prohibit. Missing entirely from the fixture set: keys, ammo, ammo packs, similar-name adversarial pairs, quantities in item scenes, and any second sample per context (all named in §33.3).
- **Remediation:**
  1. Once a real OCR engine exists (C1), generate rendered synthetic screenshots (the simulator already draws generic text scenes) at the full resolution × scale matrix with programmatic noise/blur, run the real engine, and measure per-context accuracy; store the corpus under `fixtures/screenshots/` and the measured numbers in the release report.
  2. Keep the current JSON scenes as *resolver-level* fixtures but expand to include keys, ammo, ammo-pack names, quantity text, and adversarial near-duplicate names (e.g. cartridge variants differing by one suffix token).
  3. Reword `docs/RECOGNITION.md:30` to state plainly that current fixtures are scripted OCR text exercising post-OCR logic only.
- **Release-blocking:** Yes (for any accuracy claim; the doc rewording itself is quick).

---

## HIGH

### H1. Closed extracts are recognized as ACTIVE — status is deliberately stripped, and a test asserts the wrong behavior

- **File/area:** `src/TarkovCompanion.Infrastructure/Recognition/ExtractRecognitionService.cs:81–94` (`NormalizeExtractLine`); `tests/TarkovCompanion.RecognitionTests/ExtractAndFleaTests.cs:11–41`; fixture line `"Dorms V-EX (CLOSED)"`
- **Concern:** `NormalizeExtractLine` strips the trailing status words `closed`, `pending`, `available`, `active` and then the service marks the matched extract as an `ActiveExtract` with ≥0.70 confidence. The fixture deliberately contains `Dorms V-EX (CLOSED)` and `ExtractMatcherUsesOnlyCurrentMapCanonicalExtracts` asserts it lands in the active set. So a lock-status the game explicitly displayed is discarded and inverted into "active".
- **Why it matters:** Directly contradicts handoff §26.3 ("do not falsely claim that dynamic conditions are satisfied unless observed"), §4.10 ("Never present default map extracts as definitely active unless they were actually identified"), acceptance 41.12 ("App does not pretend unknown extracts are active"), and AGENTS.md rule 19. A player routing to a closed V-EX on the strength of a companion "active, 86%" marker is the concrete harm scenario.
- **Remediation:** Add a status field to `ActiveExtract` (or a parallel `ObservedExtract` record) with `Active | Closed | Pending | Unknown` parsed from the stripped token; only render as active when the status is affirmative or absent; show closed/pending distinctly on the map; invert the test to assert `Dorms V-EX` is reported with `Closed` status and excluded from the active set.
- **Release-blocking:** Yes.

### H2. Icon fallback is structurally unsound: no reference data source, whole-frame hashing, and uncalibrated hash-as-confidence

- **File/area:** `src/TarkovCompanion.Infrastructure/Recognition/SkiaPerceptualIconMatcher.cs`; `RecognitionService.cs:67–78`; `Persistence/Migrations/0001_initial.sql:178` (`item_icon_fingerprints`)
- **Concern (four compounding defects):**
  1. **No reference source.** Nothing builds `IconFingerprintReference` sets from tarkov.dev icon imagery, and the `item_icon_fingerprints` table has no repository, reader, or writer. `docs/RECOGNITION.md:22` defers this to "the caller" — and no caller exists (C2). In production the matcher would be constructed empty or not at all.
  2. **Wrong image hashed.** `RecognitionService.cs:70` passes the *entire captured frame* to `MatchAsync`. Comparing a dHash of a full 1080p screenshot against per-icon hashes is semantically meaningless; there is no icon region-of-interest extraction anywhere (no crop from OCR bounds, no grid-cell crop).
  3. **Hash similarity presented as Confidence.** `similarity = 1 − hamming/64` feeds `Confidence` directly (`SkiaPerceptualIconMatcher.cs:51–55`). Two unrelated images average 32/64 differing bits → "0.50 confidence"; the `CandidateFloor` of 0.45 sits *below* random-noise level, so the fallback will routinely emit junk candidates at 0.45–0.65 that then merge into the shared candidate list on equal footing with OCR-derived scores (`RecognitionService.cs:71–77`). This is a textbook false-confidence channel: the numbers look like probabilities and are not.
  4. **Brittle sampling.** The hash point-samples 72 single pixels rather than area-averaging downsampled blocks, so anti-aliasing/noise on real captures flips bits freely. The "stable across resolution" test (`IconFingerprintTests.cs:27`) passes only because the synthetic gradient is analytically identical at both sizes.
- **Why it matters:** Acceptance 41.8 "Icon fallback or secondary matching works" is currently satisfiable only by test doubles. If wired as-is, it would *degrade* results by flooding the chooser with pseudo-confident noise.
- **Remediation:** Build a fingerprint sync step that renders cached item icons (license-checked) into `item_icon_fingerprints`; crop the candidate icon region (OCR line bounds neighborhood, or container cell) before hashing; replace raw similarity with a calibrated mapping that returns *no candidate* above a hamming cutoff (empirically ~10–14 of 64 for dHash) and compresses the rest into a clearly sub-OCR confidence band; switch to averaged 9×8 block downsampling; add a negative test asserting that hashing two unrelated realistic images produces no candidate.
- **Release-blocking:** Yes if the icon-fallback acceptance box is to be checked; otherwise the feature must be declared absent, not "works".

### H3. Container scanner is disconnected parts and silently drops ambiguity instead of flagging it

- **File/area:** `src/TarkovCompanion.Infrastructure/Recognition/ContainerRecognition.cs`; `ScanContext.Container` handling in `RecognitionService.cs`
- **Concern:**
  1. **No grid detection.** Handoff §15.5/§16.1 requires "detect inventory grid / item rectangles"; `ContainerGridSegmenter` requires a caller-supplied `ContainerGridSpec` (bounds + rows + columns), and nothing in the repository produces one. `docs/RECOGNITION.md:32` admits this, but acceptance 41.9 ("Mixed container simulator scene recognized") cannot pass without it.
  2. **No orchestration.** Nothing connects the Container context detection, OCR candidates, segmentation, and valuations; there is no `IContainerScanService`-style contract in `ServiceContracts.cs` at all.
  3. **Silent exclusion, no ambiguity flags.** `ContainerScanAnalyzer.Analyze` (`ContainerRecognition.cs:148–192`) drops candidates <0.70 and ignores occupied cells with no overlapping candidate. `ContainerScanResult` has **no field for ambiguous/unresolved cells**, so "flag ambiguous cells rather than silently guessing" (handoff §4.15) and "Low-confidence items must be visually marked and excluded from precise totals" (§16.2, 41.9 "Low-confidence cells are flagged") are structurally unimplementable with the current result type. `ApproximateValue` silently under-reports when cells are excluded, with no partiality indicator.
  4. **Quantities are pass-through.** `Quantity` is only ever summed from caller-supplied candidate quantities; no estimation from OCR (stack counts) exists, though §16.1 lists it.
- **Why it matters:** The one honesty mechanism the beta scanner is required to have — showing what it could *not* identify — is missing, which converts under-recognition into a confidently wrong "bag value".
- **Remediation:** Add `UnresolvedCells` (occupied, no candidate) and `AmbiguousItems` (<0.70 candidates) to `ContainerScanResult` and mark `ApproximateValue` partial when either is non-empty; implement grid detection (edge/line projection over the container panel region, or a per-resolution calibration profile) and a container orchestrator invoked from the scan use case (C2); parse `xN` stack text from cell OCR for quantities.
- **Release-blocking:** Yes for the 41.9 checklist; the result-type change should precede any UI work.

### H4. Context detection anchors and regions are unvalidated guesses; contextual OCR region can discard the item name after a *correct* detection

- **File/area:** `src/TarkovCompanion.Infrastructure/Recognition/ScanContextDetector.cs:38–63`; `OcrCoordinator.cs:44–54` (`ContextRegions`)
- **Concern:** The anchor terms ("inspect", "stash", "double press o", "filter by item", …) and their weights are hand-authored to match the fixture text; nothing ties them to observed EFT UI strings, and they are English-only with no provenance or calibration path (handoff §15.3 lists "optional user calibration"; none exists). More concretely dangerous: after detection, the contextual second pass restricts OCR to a fixed normalized rectangle — e.g. SingleItem = the central 64% × 88% of the frame (`OcrCoordinator.cs:48`). EFT inspect windows are *draggable*; an inspect panel near a screen edge yields a successful context detection (full-frame pass) followed by a contextual pass whose region excludes the item-name line, so recognition returns nothing — or resolves stray background text instead. That is worse than not narrowing at all. Handoff §15.3: "Do not rely entirely on one hardcoded pixel rectangle" — normalized fractions are still one hardcoded rectangle per context.
- **Why it matters:** In real use the highest-frequency scan (inspect) fails or misresolves whenever the window isn't where the constant assumes, with no diagnostic distinguishing "name outside region" from "no match".
- **Remediation:** Anchor the contextual region to the detected panel, not the frame: take the bounds of the anchor lines that scored the context (e.g. the "INSPECT"/title line) and OCR a region relative to those bounds; fall back to full-frame candidates when the contextual pass returns nothing. Track anchor provenance (which real UI screen/version each string was observed on) and make the anchor set data-driven (assets/calibration) so live-EFT validation (§42) can correct it without a rebuild.
- **Release-blocking:** No for a simulator-validated v1, but must be resolved before any live-EFT accuracy claim.

---

## MEDIUM

### M1. Auto-select threshold duplicated across layers; `Selected` depends on caller ordering

- **File/area:** `src/TarkovCompanion.Core/Domain/Recognition/Recognition.cs:52` (`>= 0.90` literal) vs `Infrastructure/Recognition/RecognitionPolicy.cs:15`
- **Concern:** `RecognitionResult.Selected` hardcodes 0.90 in Core while the policy constants live in Infrastructure; the two can silently drift (Core cannot reference Infrastructure, so the duplication is structural). Also `Selected` is `FirstOrDefault(≥0.90)` — correct only when the candidate list is pre-sorted by confidence; results constructed elsewhere (tests already do) could select a non-top candidate.
- **Why it matters:** Threshold semantics are the product's honesty contract (§15.6); two sources of truth invite a future mismatch between what is auto-selected and what is classified `AutoSelected`. §15.6 also says "Tune with test data" — thresholds are currently untuned constants with boundary tests only.
- **Remediation:** Move the three policy constants (and `Classify`) into Core (e.g. `Core.Domain.Recognition.RecognitionPolicy`) and have Infrastructure consume them; implement `Selected` as max-by-confidence filtered at the shared constant; record threshold tuning as pending until C3's measured corpus exists.
- **Release-blocking:** No.

### M2. Extract matching has no runner-up margin — near-tie extract names resolve silently to one winner

- **File/area:** `ExtractRecognitionService.cs:43–63`
- **Concern:** Each OCR line is matched to the single best-similarity extract; there is no minimum lead over the runner-up (unlike `ScanContextDetector`, which requires 0.10). Customs-style near-duplicate names (ZB-1011 vs ZB-1012) with a one-character OCR error can pass the 0.70 combined threshold for the wrong extract at ~0.85 displayed confidence. The combined formula (`similarity × 0.80 + ocrConfidence × 0.20`) also lets similarity 0.65 pass on the back of OCR confidence 0.9.
- **Remediation:** Require a lead margin between best and second-best extract similarity; below the margin, emit the line into `UnmatchedLines` (or a new ambiguous set) instead of activating either. Consider raising the similarity floor independently of OCR confidence.
- **Release-blocking:** No.

### M3. Resolver performance: full Damerau-Levenshtein against the whole catalog per OCR line, fresh matrices per pair

- **File/area:** `FuzzyCanonicalItemResolver.cs:49–55, 120–157`; handoff §34 targets
- **Concern:** `Resolve` scores every indexed name (items × aliases) with a freshly allocated `int[m+1, n+1]` DP matrix. A production catalog (~4,000+ items, multiple names each) × a busy frame (30–60 OCR lines, each resolved independently by `RecognitionService.ResolveOcrCandidates`) is on the order of 10⁵–10⁶ DP computations and hundreds of MB of transient allocations per scan — directly threatening the ≤1.5 s scan and ≤400 MB memory targets (§34). No performance measurement exists anywhere (release report performance section is a placeholder).
- **Remediation:** Prefilter candidates by normalized length window and shared first-token/trigram before DP (or use the SQLite FTS index for a coarse cut); cap DP string length; reuse a thread-local buffer (two rolling rows, not a full matrix); add a benchmark-style fixture test with a realistic catalog size asserting a latency budget.
- **Release-blocking:** No, but must be measured before the release report's performance section is filled.

### M4. OCR confidence semantics are assumed uniform across engines; blend weights are unempirical

- **File/area:** `FuzzyCanonicalItemResolver.cs:84` (`similarity × 0.80 + ocr × 0.20`); `ExtractRecognitionService.cs:57`; `FleaListingParser.cs:59` (`× 0.98`)
- **Concern:** All blending assumes `OcrLine.Confidence` is a comparable 0–1 probability. Real engines differ (Tesseract emits 0–100 word confidences with engine-specific meaning; Windows.Media.Ocr emits none at all). The 0.80/0.20 and 0.98 constants have no recorded basis. Since no real engine exists yet (C1), the auto-select behavior at 0.90 has never been exercised against genuine OCR confidence distributions.
- **Remediation:** Define the confidence contract on `OcrLine` (document it in Core), normalize per-engine inside each provider, and revisit the blend weights against the measured corpus from C3. For engines with no line confidence, use a documented fixed prior rather than fabricating one.
- **Release-blocking:** No.

### M5. `EstimatedUiScale` conflates resolution with UI scale, is validated circularly, and is consumed by nothing

- **File/area:** `ScanContextDetector.cs:92–106`; `ContextAndOcrTests.cs:32`
- **Concern:** Scale is estimated as median OCR line height ÷ 20 px. Text pixel height grows with *both* resolution and UI scale, so the metric cannot distinguish 4K/100% from 1080p/150%. The fixtures' line heights were authored to make the formula produce the expected value (4K lines are 30 px ⇒ 1.5), so the test proves the fixture matches the formula, not that the formula measures anything. No production or pipeline code reads `EstimatedUiScale`.
- **Remediation:** Either remove the field until something needs it, or redefine it as "text scale relative to frame height" (height ÷ frameHeight ÷ nominal) and use it to size contextual regions/OCR upscaling; validate against rendered captures, not authored numbers.
- **Release-blocking:** No.

### M6. Documentation overstates fixture coverage

- **File/area:** `docs/RECOGNITION.md:30–31`
- **Concern:** "covers 1920×1080, 2560×1440, and 3840×2160 scenes at 100%, 125%, and 150% UI scale with deterministic synthetic noise. The fixtures exercise every supported context" — factually each listed resolution and scale appears somewhere, but the phrasing implies a matrix and a meaningful noise model; the reality is 5 scenes, 15 scripted lines, one resolvable item, noise-as-metadata (C3). Handoff §33.3's required fixture families (keys, ammo, quantities, similar names, partial names) are absent.
- **Remediation:** Reword to describe exactly what exists ("five scripted OCR scenes exercising post-OCR resolution logic; rendered-pixel OCR fixtures pending"), and track the §33.3 families as explicit TODOs.
- **Release-blocking:** Yes for wording accuracy if reviews/release claim fixture coverage; trivial to fix.

---

## LOW

### L1. Debug Capture mode and scan persistence are absent (privacy-safe, but the diagnostic features are missing)

- **File/area:** `GdiScreenCaptureService.cs` (compliant: captures to memory, never persists — verified no `File`/save path in the capture or recognition code); `0001_initial.sql:178–197` (dead `item_icon_fingerprints`, `scan_history` schema)
- **Concern:** Handoff §15.7 requires an explicit opt-in local Debug Capture mode; §22.6 reserves `DebugCaptures\`; §12.8 expects `scan_history` rows. None is implemented. The default-privacy posture (no retention) is the safe side of this gap and acceptance 41.8 "screenshots are not persisted by default" currently passes vacuously.
- **Remediation:** When the scan use case lands (C2), add the opt-in debug capture writer (off by default, path under LocalAppData, excluded from support bundles unless included explicitly) and `scan_history` writes.
- **Release-blocking:** No.

### L2. Hardcoded English UI-chrome filter can eat legitimate lines

- **File/area:** `RecognitionService.cs:8–24, 110–112`
- **Concern:** `UiChromeTerms` filters exact matches and `"term "` prefixes on normalized lines. Item names beginning with a chrome term (e.g. a name starting "Price…"/"Weight…") would be silently dropped from resolution. The list is English-only and unprovenanced, same class of issue as H4.
- **Remediation:** Only skip chrome terms on *exact* match, or exempt lines that fuzzy-match any catalog name above the floor; source the list from the same calibration data as the context anchors.
- **Release-blocking:** No.

### L3. Flea parsing realism limits

- **File/area:** `FleaListingParser.cs:10–16`
- **Concern:** Rows are only parsed when a `₽`/`RUB` marker survives OCR — the rouble glyph is poorly supported by common OCR models, so real recall may be near zero until the OCR provider is charset-tuned; multi-line listing rows are not joined (documented); and the §17.2 "compare to cached market context / deal score" half of the optional feature does not exist. `NormalizeNumber` silently drops unmapped characters, though the digit-class regex mostly prevents corrupted captures from reaching it.
- **Remediation:** Note as known limitations; when a real engine lands, add a fallback for price-shaped digit groups adjacent to a currency-column region, and only then build the comparison layer.
- **Release-blocking:** No (feature is optional in v1).

### L4. GDI-only capture is the fallback path serving as primary

- **File/area:** `GdiScreenCaptureService.cs`
- **Concern:** Handoff §15.2 prefers `Windows.Graphics.Capture` with GDI as fallback. GDI `BitBlt` commonly returns black/stale frames for exclusive-fullscreen and some borderless/HDR presentation modes, so the first live-EFT validation may fail at step one. Also `SetThreadDpiAwarenessContext` is called but never restored, mutating thread state.
- **Remediation:** Add a `Windows.Graphics.Capture` provider as primary with GDI fallback (per handoff), restore the DPI context, and surface capture-provider identity in the self-test.
- **Release-blocking:** No for VM-simulator validation; revisit before live validation.

---

## Positive observations (kept brief)

- The safety boundary is respected throughout: no input synthesis, no memory access, captures stay in memory, no cloud calls, and `docs/RECOGNITION.md` states this correctly.
- `docs/BUILD_STATUS.md` leaves "Recognition" honestly unchecked, and `docs/V1_RELEASE_REPORT.md` is explicitly guarded ("Status: In progress", accuracy "populated after fixture integration") — the repository has not yet published a false claim; this review exists to prevent the checklist from being marked green on the strength of the current circular fixtures.
- Threshold boundary behavior (0.45/0.70/0.90) is explicitly unit-tested at the classification level, ambiguous results are not silently selected, and every candidate carries evidence strings and timestamps.
- Layering is correct: Core holds contracts only; the recognition code is provider-neutral; tests are deterministic and offline.

## Recommended remediation order

1. **C2** scan use case + DI composition (everything else is unreachable without a caller).
2. **C1** real OCR provider (+ self-test availability reporting).
3. **H1** extract status honesty (small, isolated, codifies the product's honesty rule).
4. **H3** container result-type ambiguity fields (do before UI consumes the type).
5. **C3/M6** rendered-pixel fixture corpus + doc rewording; only then populate accuracy/performance numbers (with **M3** measured).
6. **H2** icon fingerprint source + ROI + calibration, or explicitly descope the icon-fallback acceptance item.
7. **H4/M1/M2/M4/M5** as follow-ups before live-EFT validation.
