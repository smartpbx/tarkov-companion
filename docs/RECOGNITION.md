# Recognition pipeline

The recognition subsystem consumes only caller-supplied `CapturedImage` buffers. It does not persist frames, upload screenshots, inspect game memory or traffic, hook the renderer, or generate gameplay input. A frame remains in memory for the duration of the scan and ownership stays with the caller.

## Production OCR and availability

`TesseractOcrEngine` is the production offline provider for packaged Windows x64 builds. It uses
NuGet package `TesseractOCR` 5.5.2 (Tesseract 5.5.1/Leptonica 1.85.0) and an
embedded, SHA-256-pinned `tessdata_fast` English model. The package build target copies
the x64 native libraries into publish output. The model is atomically materialized under
the user's local application-data cache only after its hash is verified; no runtime
download occurs.

The provider exposes `IOcrEngineStatus`. Unsupported operating systems, architectures,
missing native dependencies, missing language data, and execution failures return an
explicit unavailable result and diagnostic. They never substitute scripted OCR.
`RecognitionSelfTest` exposes OCR status, canonical-catalog count, and the intentionally
disabled icon fallback as structured self-test data. The runtime composition owner must
include that result in the App's existing `--self-test` report; this recognition change
does not take ownership of App startup or diagnostics composition.

License, source revision, redistribution, VC++ runtime, and model provenance are recorded
in `docs/LICENSING.md` and `docs/THIRD_PARTY_NOTICES.md`. The full Apache-2.0 text is in
`LICENSES/Apache-2.0.txt`.

## Pixel-to-result flow

`OcrCoordinator` runs a full-frame OCR pass, detects the visible context from the
data-driven `Recognition/anchors.en.json` catalog, and runs a second OCR pass in a region
relative to the matched anchor bounds. Every anchor carries provenance that currently
labels it as simulator-derived and live-unvalidated. Full-frame lines are merged back into
the candidate set, so a draggable panel or imperfect contextual crop cannot discard text
that the first pass already observed.

`SqliteRecognitionCatalogRepository` builds canonical item references from the synchronized
`items` table, including short names as aliases. Production callers do not supply handcrafted
item lists. `CanonicalItemResolverCache` indexes that catalog once, and the fuzzy resolver
uses exact/trigram prefiltering plus bounded edit distance. A 5,000-item/40-line performance
test includes catalog construction and is bounded by the 1.5-second scan budget.

`ScanUseCase` in Application owns capture -> context recognition -> context dispatch ->
recommendation -> metadata persistence -> result publication. It dispatches:

- single items to canonical lookup, cached item/price lookup, and `IRecommendationEngine`;
- extract lists only against the current raid map's canonical extracts;
- mixed containers to grid detection, occupied-cell OCR, quantities, valuation, and partial totals;
- visible flea rows to local parsing only.

`LatestScanResultPublisher` is the runtime/UI seam. `SqliteScanEventRepository` writes UTC
metadata, candidate evidence, recommendation, source geometry, and diagnostics to
`scan_history`; it has no captured-pixel or image column.

### Runtime registration seam

The App/runtime owner should construct this graph after SQLite migrations have run:

1. Register one `TesseractOcrEngine` as both `IOcrEngine` and `IOcrEngineStatus`.
2. Register `SqliteRecognitionCatalogRepository`, `SqliteScanEventRepository`, and one
   `CanonicalItemResolverCache` using the existing `SqliteConnectionFactory`.
3. Construct `ScanContextDetector` (its default constructor loads the bundled anchors),
   `OcrCoordinator`, `RecognitionService`, `ExtractRecognitionService`,
   `ContainerRecognitionService`, and `FleaRecognitionService`.
4. Register one `LatestScanResultPublisher`; UI state may read `Current` and subscribe to
   `Published` without taking ownership of recognition orchestration.
5. Construct `ScanUseCase` with the existing capture, map, raid-state, item,
   recommendation, and recommendation-context services plus the recognition services,
   scan-event repository, and publisher above.
6. Invoke `IScanUseCase.ScanAsync(new ScanRequest(captureRequest), cancellationToken)` from
   the runtime worker/hotkey path. Include `IRecognitionSelfTest.RunAsync` capability rows
   in the existing App self-test report.

No fixture OCR type exists in production assemblies. The runtime must not register a
scripted substitute when `IOcrEngineStatus.Availability.IsAvailable` is false; the scan use
case will publish an honest unavailable outcome.

## Confidence and honest partial state

OCR confidence is normalized by the provider to 0..1 for ranking output from that provider.
It is not presented as a calibrated probability. Shared Core thresholds are:

- `>= 0.90`: eligible for automatic selection only with at least a `0.08` runner-up lead;
- `>= 0.70` and `< 0.90`: ambiguous;
- `>= 0.45` and `< 0.70`: candidate only;
- `< 0.45`: no match.

Candidate selection is confidence-ordered and independent of input order. Every candidate
retains evidence, bounds, and the capture timestamp in its containing result.

Extract observations preserve `Active`, `Closed`, `Pending`, or `Unknown`. Closed and pending
observations never enter `ActiveExtracts`; near-tie names remain ambiguous. A line without an
explicit status is treated as active because that is the simulator contract, and the evidence
retains that it came from OCR rather than live state.

Container totals exclude unresolved and ambiguous occupied cells. Results expose both cell
sets and `IsPartial`; missing valuations also make the total partial. Quantities such as `x2`
or `qty: 2` are parsed from cell text. The deterministic grid detector searches for a regular
line lattice, then the segmenter measures occupied cells from in-memory luminance variation.

Flea parsing reads visible rouble/RUB rows and optional quantities. It never buys, sells,
clicks, types, contacts a market service, or claims that a listing remained available after
the captured timestamp.

## Icon fallback

Production icon fallback is explicitly unavailable. No licensed runtime-cached icon
fingerprint repository is currently populated, so `RecognitionService` does not invoke an
icon matcher and `RecognitionSelfTest` reports the capability disabled. The retained
experimental ROI matcher uses averaged dHash with a hard 12-bit negative cutoff and caps its
ranking score below the ambiguity threshold; its raw distance is labeled as not a probability.

## Fixtures and evidence

The suites deliberately separate two evidence levels:

- `fixtures/recognition/synthetic-scenes.json` contains scripted OCR lines. These tests cover
  post-OCR normalization, anchors, dispatch, ambiguity, status, and policy logic only. Their
  resolution, scale, and noise metadata are not OCR-accuracy evidence.
- `RenderedPixelOcrTests` creates actual high-contrast bitmap scenes with deterministic pixel
  noise for a 1080p item, similar-name ambiguity, 1440p extract statuses, a 1440p mixed
  container, and 4K flea rows. The tests call `TesseractOcrEngine`, not a fixture engine.

Rendered-pixel tests are discovered as explicit skips on non-Windows-x64 hosts, with the
reason reported by the test runner. On Windows x64, provider absence is a failure and every
scene must pass. Therefore a Linux green run proves packaging, post-OCR behavior, persistence,
and skip honesty; it does not publish screenshot-recognition accuracy. Accuracy remains
unmeasured until the Windows suite or VM smoke run records real results.

The anchors and rendered scenes remain synthetic and English-only. Live validation across
EFT themes, localization, HDR, ultrawide layouts, and UI revisions is still outstanding.
No output from this subsystem should be described as a live detection or gameplay-state guarantee.

## Capture sessions (v2 checkpoint)

Capture sessions are an Application-owned intake and review boundary for pixels the player
explicitly supplied, pasted, selected, or caused the game to write. An armed intent is claimed
atomically when intake accepts the next submission, before bounded-queue waiting; the single
preparation reader preserves intake order, while independent review waits do not occupy that
reader or a runtime-supervisor CPU slot. Every decoded buffer has one `CapturePixelLease` owner,
counts against the configured pixel budget, and is zeroed on acceptance, duplicate rejection,
cancellation, timeout, analysis failure, retry, and shutdown.

Artifacts preserve delivery and source kind, capture/submission UTC, correlation and batch IDs,
the frozen workspace context, canonical `ProfileContext` when supplied, confidence, review
correction, and decode revision. Content digests are process-local dedupe keys only: they are not
published or persisted, are bounded and expired, and are cleared when review requests a retry.
`UseArmedIntent` is carried to consumers as an explicit decision and effective intent; the
detector result remains available as evidence rather than being silently rewritten.

This checkpoint does **not** register capture sessions in production. `SupervisedCaptureWorkScheduler`
is the adapter to the #268 runtime supervisor, but App composition issue #294 still owns creating
the concrete recognition pipeline, review UI, accepted-result consumer, and canonical active
`ProfileContext` provider. Until that composition lands, `RaidObservationService` receives no
`ICaptureSessionService`, and the established `ScanUseCase`/HUD path remains authoritative. The
bounded screenshot decoder from OCR PR #333 is also an integration dependency; its deadline,
pixel cap, encoded-buffer lifetime, and authoritative retry policy must survive reconciliation.
