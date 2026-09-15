# Recognition pipeline

The recognition subsystem consumes only caller-supplied `CapturedImage` buffers. It does not persist frames, upload screenshots, inspect game memory or traffic, hook the renderer, or generate gameplay input. A frame remains in memory for the duration of the scan and ownership stays with the caller.

## Production OCR and availability

The Windows build prefers `WindowsMediaOcrEngine`, the recognizer shipped with Windows 10 and
later, and falls back to `TesseractOcrEngine`. Tesseract uses NuGet package `TesseractOCR` 5.5.2
(Tesseract 5.5.1/Leptonica 1.85.0) and an embedded, SHA-256-pinned `tessdata_fast` English model.
The package build target copies the x64 native libraries into publish output. The model is
atomically materialized under the user's local application-data cache only after its hash is
verified; no runtime download occurs.

Windows Media OCR uses a 2,600-pixel compatibility tile edge, or the installed component's
reported native limit when that is smaller. The compatibility edge is fixed because Windows
versions report different maxima, while the 4K seam and diagnostic contract must not change
with the runner image. Oversized 4K and ultrawide source regions therefore use deterministic
128-pixel-overlap tiles at native resolution; no source pixel is dropped to make the frame fit.
Every tile result is translated back to source-image coordinates, and equal or contained text
over the same source geometry is deduplicated deterministically. The provider publishes no
confidence score. Its lines carry `null`, which is unscored evidence and remains distinct from
a provider reporting numeric zero.

Both providers enforce a 15-second caller-visible frame timeout, 40-million-source-pixel and
192-MiB input-buffer ceilings, and a 256-MiB estimated peak-memory ceiling. Tesseract also caps
its prepared grayscale image at 40 million pixels; Windows caps one request at 64 bounded tiles.
The Windows estimate counts the caller's source buffer plus both copies of the largest tile, the
managed BGRA staging buffer and the `SoftwareBitmap` copied from it; Windows' own recognizer
working memory is not observable and is not included. Each provider accepts at most 4,096
non-empty lines per request and keeps at most 1,024 characters of any line; exceeding either is a
partial result (`ocr_line_limit_exceeded`, `ocr_line_text_truncated`), never a silent cut.

Both providers serialize their native work behind one gate. Caller cancellation remains
cancellation, but neither a caller-visible timeout nor a cancellation releases the gate while the
abandoned native read is still running: the gate opens only once that work settles, and the same
settle path observes a late failure. A later request that cannot obtain the gate within its own
frame timeout reports `ocr_frame_timeout` with zero attempted tiles. An out-of-memory failure stops
the request instead of allocating the next tile (`ocr_memory_exhausted`). Preparation checks
cancellation every 65,536 pixels, including Tesseract's bright-text midpoint scan, so a very wide
single-row region cannot run to the end before noticing.

Tesseract takes its page reader, checks for disposal and registers the native read as one step
under its lifetime lock. `Dispose` frees the reader only when it finds no registered read under
that lock, and otherwise leaves freeing it to the read's completion, so a read can neither start on
a freed reader nor have its reader freed underneath it. A request that a concurrent `Dispose`
overtakes before its read is registered throws `ObjectDisposedException`, as a request on an
already-disposed provider does, rather than reporting an unavailable provider.

A request can queue on the gate behind native work an earlier request abandoned, and that work can
fail after its caller has gone. Clearing the abandoned work and, when it failed, retiring the
provider (marking it unavailable and freeing its reader once nothing native uses it) are one step
under the lifetime lock, taken before the gate is released. A request rechecks disposal and
retirement as soon as it owns the gate and again when it registers its read, so the queued request
answers `ocr_provider_unavailable`, or throws `ObjectDisposedException`, without preparing a frame
or starting a read on the failed engine; the late failure used to leave the reader in place, and the
queued request read it. `TesseractOcrEngine.WaitForSettledAsync` waits, bounded, until the provider
is idle, which is the point at which its availability reflects how abandoned work ended.

A timeout, rejected input, unavailable provider, total provider failure, and a result recovered
from only some tiles have separate diagnostic outcomes. So does an available read that ran to
completion and found no text: its status is `Empty` and its diagnostic `ocr_no_text`. A requested
region that clamps to no pixels is also an available `Empty` read, with `ocr_region_empty`, in both
providers and after the same unavailable and input-limit checks; Tesseract used to throw for it,
so a contextual crop clamped to nothing ended the scan. Neither is `Complete` or unavailable.

The detailed provider results report source region, source and prepared dimensions, scale,
planned, attempted and completed tile counts, duration, provider, estimated peak bytes, accepted
and truncated line counts, and exact diagnostic outcome. A tile skipped because the deadline had
already passed is planned but not attempted, and only attempted tiles are listed. They contain
neither source pixels nor source paths.

`SkiaScreenshotImageLoader` checks a screenshot file's length against a 64-MiB ceiling before any
buffer for it exists, reads no more than that checked length, refuses a picture over 40 million
pixels before decoding it, and returns only a complete decode. An incomplete PNG, such as one the
game is still writing, is refused rather than returned with blank rows.

One 15-second load deadline (`ScreenshotImageLoaderOptions.Timeout`), linked to the caller's token,
starts before the file is opened and covers the read, the pixel buffer and the decode. A token that
is already cancelled throws before the file is opened. Expiry returns no frame, the same answer as
a file that would not decode; caller cancellation throws. The decode runs on its own thread, which
owns the codec and the pinned pixel buffer until native code returns: a caller that gives up
returns at once while the buffer native code writes into stays alive, and the pixels of a decode
that finishes after its caller left are cleared. Decodes are serialized, and an abandoned decode
keeps the gate until it settles. Cancellation is checked between bounded decode chunks where the
codec allows it, measured against SkiaSharp 3.119 rather than assumed: JPEG decodes top-down
scanlines at most 65,536 pixels (or one wider row) per call; PNG decodes incrementally while its input is handed over
64 KiB at a time, so a call inflates at most one input chunk; WebP offers neither and decodes in
one native call that the deadline still bounds for the caller. The chunked decodes reproduce Skia's
one-call decode byte for byte in the tests.

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
that the first pass already observed. Overlapping full/contextual reads use the same
geometry-aware deterministic deduplication rule as tiled Windows reads; the source file is
compiled into both assemblies. It compares a line only with kept lines sharing a 64-pixel grid
cell, and every comparison spends from a budget of 64 per input line. Exhausting the budget keeps
every remaining line and reports `ocr_dedupe_budget_exhausted` rather than dropping evidence or
going quadratic. An available empty full-frame read surfaces as `CoordinatedOcrResult.IsEmpty`
with `ocr_no_text`, which `RecognitionService` reports instead of `context_unknown`. A frame that
timed out or was rejected keeps its own diagnostic instead of being reported as an unavailable
provider.

A partial or otherwise degraded provider read keeps its diagnostic all the way to the scan, for
every context a scan dispatches to. The merged candidate set carries the degradation of whichever
pass fed it (the contextual pass first, then the full frame, then an exhausted dedupe budget), where
a partial pass that was still available used to contribute its lines and lose its code.
`RecognitionService` gives that code precedence over every code derived from the same read,
including `context_unknown`, `no_match`, `extract_context` and the absent code of an auto-selected
item. `ExtractRecognitionService` carries the degradation of its full-frame reading, then of its
panel reading, including a panel reading that failed or timed out outright, ahead of
`extracts_partial`; `FleaRecognitionService` carries its read's ahead of `no_visible_flea_rows`.

`ScanUseCase` treats any recognition code that is not a known conclusion about complete text
(`context_unknown`, `extract_context`, `item_not_auto_selected`, `no_match`, `ambiguous`,
`ambiguous_runner_up`, `low_confidence_candidates`, `ocr_no_text`, `ocr_region_empty`) as a
degradation, and any code at all on an auto-selected item. A degradation makes every dispatch at
best `Partial` and leads the scan's code, joined with the dispatch's own where it adds one:
`ocr_partial_tiles; canonical_item_or_price_unavailable`. Each dispatch used to decide the scan from
its own reading, so a context found in a degraded frame followed by a clean extract, container or
flea read published `Complete`, and a single item whose price lookup failed had the degraded code
overwritten. The list is of harmless codes on purpose, so a degradation code nobody has listed makes
a scan `Partial` rather than `Complete`. An extract, container or flea result that carries any code
also makes the scan `Partial`. `ScanUseCase` still prices and advises on an auto-selected item from
degraded text.

One captured frame has one budget. `ScanUseCase` starts a 30-second `ScanFrameDeadline`
(`ScanFrameOptions`), linked to the caller's token, and every stage that reads the frame spends from
it: recognition's full-frame and contextual passes, the catalog load and line resolution,
the extract recognizer's frame and panel passes and its matching, the flea pass and parse, container
grid detection, segmentation, whole-grid pass, cell fallbacks, resolution, analyses and price
lookups, and the scan's map, item, price and recommendation-context lookups. It used to be scoped per
component: recognition spent thirty seconds, the extract and flea passes and every lookup then ran on
the caller's token, and the container recognizer started a fresh thirty seconds with its resolver,
analysis and price lookups outside it, so one capture could spend a minute and then some. A
component handed the frame's token joins its deadline (`OcrPipelineDeadline`) rather than starting a
budget of its own; called outside a scan, `OcrCoordinator`, `ExtractRecognitionService`,
`FleaRecognitionService` and `ContainerRecognitionService` each start one (`OcrPipelineOptions`, 30
seconds) that the components they call join in turn, so a budget is never nested inside another.
Each provider call's own frame timeout remains an inner cap, and each stage is checked before it
starts as well as during it, so a repository that ignores its token still does not start a lookup
the budget has ruled out.

Expiry is a measured outcome, not cancellation: whatever finished is kept and the stage that was
cut says `ocr_pipeline_timeout`; caller cancellation still throws. A recognition cut before it
finished is an unknown-context `Partial` scan; a dispatch or lookup cut keeps the recognition. The
extract recognizer matches the frame's lines before reading the panel, so a cut panel keeps the
exits the frame named; a cut frame pass is not available with `ocr_pipeline_timeout`, which the scan
reports as `Partial` rather than as a missing provider, and writes nothing to the raid, where an
empty list would have replaced the exits the last good scan found. The flea recognizer keeps the
rows parsed before a cut. Recording is not reading: the scan event, the publication and the raid's
extract write use the caller's token, so a frame that ran out of budget still records what it found
and that it stopped.

A frame pass that ran out of memory is not followed by a contextual pass, or by the extract panel
pass. Container grid detection walks the whole frame and used to run before any deadline existed; it
and segmentation check cancellation in bounded chunks, and the budget running out during them, or
before the whole-grid analysis finishes, returns an empty partial result with
`ocr_pipeline_timeout`. After that analysis, a cut cell fallback, price lookup or final analysis
returns the last analysis that finished, marked partial with `ocr_pipeline_timeout`. A degraded
whole-grid pass or cell read marks the scan partial and names it: the deadline first, then the
whole-grid pass, then cells in reading order.
Each unread cell issue whose own read was degraded, or whose planned read the deadline or an
out-of-memory cell read left unattempted, keeps its reason and gains `; ocr=<code>`.

Grid detection, segmentation and stash discovery refuse a frame over 40,000,000 pixels, the
providers' default source ceiling and the screenshot loader's, before reading a pixel of it; the
container recognizer answers such a frame with `ocr_input_limit_exceeded`, and the recognizer skips
its HUD probe. The providers refused such a frame, but grid detection walked it before any provider
was asked. A whole-grid pass or cell read
that ran out of memory is not followed by further cell reads. These deadline, cancellation and
diagnostic changes are #299's; the grid detection, segmentation and analysis algorithms remain
owned by #273.

Character/health menu captions and the game-version strip are supplemental full-frame text
signals. They are searched across every returned line, with their observed bounds reported only
after a match. No location crop is a prerequisite, because window shape and UI scale can move
both surfaces. These are measured text signals for later v2 adapters, not a claim that limb
health or game state was inferred.

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

Rendered-pixel tests are discovered as explicit skips on non-Windows-x64 hosts, with the reason
reported by the test runner. The dedicated `TarkovCompanion.Platform.Windows.OcrTests` project
also renders text beyond the first native Windows tile and calls `WindowsMediaOcrEngine`
directly; it does not substitute a fixture or Tesseract for the provider that normally ships.
Its fixture-recognizer tests prove the Windows gate, both-copy memory estimate, out-of-memory
stop, and line bounds on the Windows runner. The Tesseract gate, deadline, pixel and memory
ceilings, empty and empty-region outcomes, line bounds, chunked cancellation, and start/dispose
atomicity (a `Dispose` forced into the window between taking the reader and registering its read,
one forced during preparation, and 200 scheduler-chosen interleavings) are proven on every host
through an internal page-reader seam that production composition cannot reach. So is the late
native failure of an abandoned read: queued behind a timeout and behind a cancellation (the queued
request answers unavailable and never reads), with the provider disposed while a request is queued,
and against the settle wait, with encoded pages checked unchanged across abandoned reads.
`OcrProbeSettleTests` drives the probe through the same seam for a last pass whose read fails late, a
read still unsettled at the bound, and a pass queued behind a late failure.

`ScanDispatchDiagnosticsTests` runs a scripted provider through the real coordinator, recognizer,
extract, container and flea recognizers and `ScanUseCase`, degrading each pass of every dispatch
(recognition frame and context, extract frame and panel, container grid and cell, flea) as an
available partial read and as an unavailable one: none publishes `Complete`, each keeps its code
through publication, persistence and evidence, the single-item early failures keep the reading's
code beside their own, and the same screens read cleanly publish `Complete`. `ScanFrameBudgetTests`
spends each pass and lookup on a manual clock and proves, for a single item, an extract list
(including its panel), flea rows and a container (including its price lookups), which stage the
budget cut, what was kept, that every stage received the one frame token, and that the frame's is
the only timer started. `PixelCeilingTests` proves the ceiling refusals read no pixel. The loader's
chunked decode, its cancellation and its abandoned-decode ownership run against Skia's Linux native
assets in `TarkovCompanion.UnitTests`.
On Windows x64, provider absence is a failure. Therefore a Linux green run proves compilation,
post-OCR behavior, persistence, and skip honesty; it does not publish screenshot-recognition
accuracy. Accuracy remains unmeasured until Windows CI records the provider result, and no
synthetic result is a benchmark threshold.

## Local OCR probe

`--ocr-probe <image>` compares the preparations each production provider supports. Add
`--ocr-probe-region x,y,w,h` to select a fractional source region and `--output <json>` for a
`tarkov-companion.ocr-probe.v1` machine report. Full-frame machine reports omit OCR text; their
context, timing, bounds, confidence availability, tile, memory, and provider evidence remain.

`--ocr-probe-cells` passes the selected region through `StashGrid.Cells`, reads at most 256 of
the returned captions with both production providers, prints their local comparison to stderr,
and writes one machine-readable JSON document to stdout or `--output`. When the grid returned more
cells, the report says `ocr_probe_cell_limit_exceeded` and carries `detectedCellCount`.

Both probe modes spend one two-minute deadline across every provider pass, and the cell mode also
spends it on the stash-grid search, which reads the whole region once per axis and used to run
before the deadline started. When it expires the report keeps the finished passes and says
`ocr_probe_deadline_exceeded`; expiry during the grid search reports no cells, no
`detectedCellCount` and no passes. Each provider entry records its planned, attempted and
completed pass counts, so skipped work stays visible. A provider whose pass reports
`ocr_provider_failed` or `ocr_provider_unavailable` stops its own remaining passes and records that
code as the entry's `diagnosticCode`; one that marks itself unavailable stops the same way with
`ocr_provider_failed`. Other providers still run. Each entry's availability and reason are read
again after its passes rather than assumed, and only once native work its passes abandoned has
settled: a pass that timed out, or that the run deadline cancelled, leaves its read running, and a
failure that read reports later retires the provider. The report used to record availability before
that failure landed and say available with no diagnostic. It now waits for the provider to be idle,
bounded by `OcrProbeLimits.SettleTimeout` (30 seconds, separate from the run deadline that may have
abandoned the work), records a late failure as unavailable with `ocr_provider_failed`, and records
work still running at the bound as `ocr_provider_settle_timeout` rather than as a healthy provider.
A report is written beside its destination and moved over it only once complete, so cancellation
at any point leaves the previous file, or no file, rather than a truncated one. Ctrl+C cancels the
probe at its next bounded check and exits with code 130 without writing a report; a second Ctrl+C
terminates the process normally. Cell reports retain caption OCR text for
local comparison. They contain no screenshot path, filename, pixels, username field, token,
world coordinate, benchmark threshold, or claimed accuracy. The schema is a stable producer
result owned by the OCR path rather than an implementation of #272's provisional corpus/scorer
schema; its nullable confidence is intentional. Probe reports are local diagnostic material and
must not be committed or uploaded as CI artifacts.

The anchors and rendered scenes remain synthetic and English-only. Live validation across
EFT themes, localization, HDR, ultrawide layouts, and UI revisions is still outstanding.
No output from this subsystem should be described as a live detection or gameplay-state guarantee.
