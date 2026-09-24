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
relative to the matched anchor bounds. Every anchor carries provenance; legacy anchors remain
labelled simulator-derived and live-unvalidated until measured on player frames. Full-frame lines are merged back into
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

TASKS recognition uses hand-transcribed 2026-09-22 player frames: list pages require the
STORY/SIDE/OPERATIONAL tabs plus table headers, while STORY chapters require objective headings.
Outside a raid, recognised TASKS files form a two-minute burst and raise one review-only sync offer.

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

V2 capture-session recognition has a five-minute outer supervisor safety bound. Its former
45-second bound discarded valid real-frame grid results that took 64 seconds unloaded and up to
130 seconds in paired loaded runs; explicit caller and shutdown cancellation remain immediate.

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

The HEALTH tab draws the stash, pockets and backpack beside the body, so the anchors alone score
it Container at 1.0. `HealthScreenClassifier` places it first (four of the seven limb captions and
three "35/35" values; transcribed from the real frame of 2026-09-22, not yet measured on Windows
OCR), and it and the extract list reach a "not supported yet" answer instead of a grid (#287).

**On three real in-raid Gear screens (2026-09-20, Windows OCR, from Clayton's log).** Context was
never ambiguous: all three scored Container at 0.70, which only "tactical rig" + "backpack" +
"pockets" (0.25 + 0.25 + 0.20) sum to, so those three anchors are marked live-validated. The
`ambiguous_runner_up` with five candidates on the same log line is the single-item name ranking
run over a container screen's text, not the context. The character captions read present on one
frame and absent on another 14 seconds later: the tab row can come back as one line, and a whole
line had to equal "health". Captions are now matched word by word on lines of up to eight
words. The version strip read absent on all three, though it is there: it is 10 to 14 pixels
tall with a peak luminance of 77 on a background of 11 (113 on 35 over a bright scene), which
the engine likely drops; nothing reads this signal yet. Unmeasured until an OCR probe of that
corner runs on Windows.

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
- visible flea rows to local parsing only. In V2 an armed Flea intent (or a screen the anchor
  detector reads as the flea) runs `FleaRecognitionService` from `CaptureRecognitionPipeline`; the
  rows ride on `CaptureAnalysis.FleaListings` to `FleaCaptureHandoff`, and Intel > Flea shows each
  beside the best trader price and the 24-hour average after its fee. It reads the screenshot the
  player took and asks the market nothing.

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

Flea parsing reads visible rouble/RUB, euro/EUR, and dollar/USD rows, optional quantities, and
labelled durability, uses, charges, or resource values. Foreign quotes are converted with the
current catalog's documented RUB rate; a row is left unresolved when that rate is unavailable.
It never buys, sells, clicks, types, contacts a market service, or claims that a listing remained
available after the captured timestamp.

A row is joined by geometry, not by OCR line order. Each price (a number with a rouble sign or
`RUB`, the two read back together when the engine returned them as separate lines on one visual
line) is a row; every other line attaches to the price nearest vertically, and to none when two
prices are equally near or when it lies further than 2.5 line heights from any. A quantity
("x3", "×3", "qty 3") is taken from the price's own line or an attached one; two different
quantities on one row read as none. Bounds cover the price and everything attached, and
`FleaListing.SourceText` keeps the lines a row was joined from, for review. The 2026-09-22
3840-by-1080 flea capture confirms the multi-row layout and a euro-denominated offer, but the true
row pitch, how the OCR engine renders a lost rouble sign, and the condition column still require
fixture-backed pixel measurement. A bare number is never taken for a price.

## Icon fallback

Production icon fallback is explicitly unavailable. No licensed runtime-cached icon
fingerprint repository is currently populated, so `RecognitionService` does not invoke an
icon matcher and `RecognitionSelfTest` reports the capability disabled. The retained
experimental ROI matcher uses averaged dHash with a hard 12-bit negative cutoff and caps its
ranking score below the ambiguity threshold; its raw distance is labeled as not a probability.

## V2 pixel-to-grid reconstruction (#273)

`GridPixelReconstructionBuilder` (Infrastructure/Recognition/Grid) is the V2 counterpart that
turns a captured frame into a `GridReconstructionRequest` for `InventoryGridReconstructor`, run
from `CaptureRecognitionPipeline.AnalyzeAsync` while pixels are still available and carried
pixel-free on `CaptureAnalysis.Grid` from there on. A stack badge is read through the shared OCR
API for quantity only; item name text is never read for identity.

Package 37 measured the whole path, screenshot to decision, and rewrote the three steps the
numbers showed were broken. How each one works now:

- **The lattice.** `ContainerGridDetector` looks for thin ridges that run unbroken for about a
  cell, wherever in the frame they are, first at the measured pitch (63 pixels on a 1080-tall
  frame, held to two pixels and one phase, so a run cannot leave its own panel) and only then
  at whatever pitch the lines suggest. It used to want contrast across 18% of the whole frame,
  which a ten-wide stash satisfies and a loot container cannot. A line hidden from top to bottom
  by wide items is assumed (up to two in a row), runs are scored by pixels of long line so the
  ribs of a rail do not outvote real rows, and the two axes are held to one pitch because cells
  are square.
- **Footprints.** `GridBorderProbe` reads the border drawn between neighbouring cells. No line
  between two cells means one item; a line means two. Joining every occupied cell that touched
  another read two bandages side by side as one 2x1 item.
- **Identity.** The 64-bit difference hash no longer decides or shortlists anything; it is kept
  only as the fallback when a reference's pixels cannot be read.
  `IconPixelDescriptor`, a 16-pixel-a-cell colour picture compared by normalised correlation
  against every reference of the footprint's shape, decides: at least 0.85 and 0.04 clear of the
  next item, or the cell is refused with its lookalikes attached. A named item carries that score as its evidence confidence. A non-square footprint
  is also compared turned a quarter each way. `IconReferenceIndex` keeps the reference list in memory between scans.

`IconEvidenceIndexer` fills the icon evidence cache (#355) from the catalog's grid images after
each sync, on this machine only, as ADR 0007 allows. Until it has run, every cell is refused.

**Measured on composed frames.** `LootScanEndToEndMeasurementTests` composes frames out of
json.tarkov.dev's own grid images with the truth known by construction (1080p, dimmed with
noise, resampled to 1440p, and 3840x1080) and drives them through a real capture session, the
builder, the handoff and the decision service. Every figure from it is a ceiling: the art is the
very bytes the index was built from. On 72 frames and 1,022 items it finds the grid in 69, gets
997 footprints right, names 650 and names none wrongly. It needs the local icon corpus
(`TARKOV_ICON_CORPUS`, by default `/root/orca/recognition-corpus/icons`) and skips without it.

**Measured on real screenshots (2026-09-18).** Nine 3840x1080 hideout screenshots of Clayton's
stash and two opened cases, six of them hand-labelled from the game's captions (360 items;
sidecars beside the screenshots, outside git). `RealScreenshotMeasurementTests` writes what the
recognizer made of each frame as native-size overlays; `IdentityPolicyStudyTests` scores naming
policies on the labelled cells and on composed cells side by side.

- The generic detector returned a lattice across several panels in 8 of the 9 frames. Held to
  the measured pitch it lands on one panel at the exact phase in all 9.
- Identity: 269 cells named across the nine frames through the generic path, every one checked
  against its caption, none wrong. On the 360 labelled items the policy names 163 (45%), against
  63% on composed frames. Real true-item scores run from under 0.4 to 0.99, median 0.83; dark
  attachments have a median of 0.42 and the true item is on top for only 27 of 68.
- The floor was 0.90, chosen on composed frames, which cannot test it (every floor from 0.50 to
  0.90 names the same composed items). On real pixels 0.90 names 118, 0.85 names 163, 0.80
  names 200, all with none wrong, and 0.75 names 215 with 2 wrong. It is now 0.85.
- The difference-hash shortlist dropped 27 of 360 true items before they were compared and
  caused the only wrong name at 0.85, so every reference of the footprint's shape is compared.
- Masking the caption and badge bands lifts a dark item's true score from 0.4 to 0.9 and starts
  naming wrong items, because a caption is sometimes all that separates two icons. Not used.

These are menu screens. They do not settle hover or selection highlights over a raid container,
freshly looted found-in-raid state, or which panel is the container on the in-raid loot screen.

### What is not built, and the pixels it is waiting for (2026-09-19)

Two things #273 asks for, and the limb and gear-slot reading #305 asks for, are not built. Each
was looked at against the real frames first, and in each case the frames are too few or the
wrong screen. What they do show is measured in `docs/research/EFT_SCREENSHOT_FACTS.md`.

- **The carried grid.** Built 2026-09-22 for the in-raid Gear screen; see "The in-raid Gear
  screen" below. Still wanted: at least three different backpacks and two rigs, a multi-grid
  backpack, the carried column scrolled, a part-full bag, 1920x1080, and an OCR run on Windows
  over the headers.
- **Rotated items.** Matched since 2026-09-22 (a non-square footprint is also compared turned a
  quarter each way). One real rotated item so far, an RSP-30 flare: its colour twins are within
  the margin, so it is refused with the right item on top. *Wanted:* more rotated items, labelled.
- **Limb health (#305).** The in-raid HUD silhouette is refused on measurement (its outline peaks
  at 83). The Gear tab draws no limb health. *Wanted:* the HEALTH tab, healthy and with genuine
  injuries, a blacked limb, and over a dark and a bright scene; and one in-raid HUD frame of a
  genuine injury over a dark background, which the 261 in-raid frames do not contain.
- **Gear-slot occupancy (#305).** Measured and clearly separable on one loadout (an empty slot's
  brightest pixel is 56 to 64, an occupied one's 209 to 255), and not built: eight occupied
  slots and three empty ones from one loadout are a measurement, not a threshold, and the frozen
  V2 contract has limb regions and no gear slots to report into. *Wanted:* Gear tabs with other
  loadouts, each slot both ways, a dark item in every slot, and 1920x1080.
- **The vitals strip (#305).** Total health, hydration and energy are drawn bright (peak 224)
  on the Gear tab, so they are the legible kind of text. Not read, because OCR of them has not
  been measured. *Wanted:* an OCR run on Windows over these same nine frames, which needs no
  new screenshots.

### The in-raid Gear screen (2026-09-22)

Three real 3840x1080 in-raid Gear screens (2026-09-20, `/root/orca/incoming/loot-2026-09-20`,
never committed): A, a wooden ammo box open beside a full Duffle; B, an unsearched Duffle with a
tooltip over the carried backpack; C, a scav's own inventory with no loot open.

- **What was wrong.** The general line detector returned an 11x6 lattice across the rig, pockets
  and backpack on A, and a 3x2 corner of the pouch as "loot" on C. No backpack was read at all,
  so every take was "TAKE?".
- **`GearScreenLayoutReader`.** Every grid on this screen has its own one-pixel frame, grey
  (88, 93, 96), with near-black outside it; the lines between cells are darker. A grid is a top
  and bottom frame run of the same start and length, a whole number of 63-pixel cells apart,
  with the left frame drawn between them. It found every grid whose frame is in view on all
  three frames (14, 12, 13), none spurious, in 11 to 27 ms. Sections come from the layout:
  loot right of the carried column, pockets on the slot column, the rig above them, the
  backpack in the first group below when it starts where its header puts it (44 pixels below
  the pockets on A and B), otherwise "other" (C's pouch). On eleven later real frames, a
  pitch-aligned right-edge recovery plus a scoped inner-lattice fallback finds all nine visible
  backpacks, including the tooltip, packed and low-panel cases; all rig and pocket pouch counts
  and dimensions match their hand-read labels.
- **Loot.** A: the 3x3 box, one 1x1 footprint, named right (7.62x39 SP ammo pack, 0.905). B and
  C: no loot lattice, where the old path read carried grids as loot.
- **Backpack.** A: 4x3 at the exact phase, 7 footprints, all 7 the right shape. Named 1 (Poxeram,
  0.938), refused 6, none wrong. The true item is on top for 5 of the 6 refused: XCEL 0.802 and
  Hot Rod 0.834 under the 0.85 floor, the Rotor 43 at 0.674 with two variants near, the rotated
  RSP-30 beside its colour twins. The M-LOK rail and Kobra mount score under 0.6.
- **Lattice score.** A Gear-screen lattice carries the share of its frame that was drawn as
  its evidence score. The planner acts only on scored evidence, and an unscored lattice made
  every carried grid "capacity unavailable".
- Only the backpack's largest grid is planned against; the rig and pockets are found and not
  used. `RealGearScreenMeasurementTests` reports all of this per cell against hand labels kept
  beside the screenshots; `RealLootFrameEndToEndTests` runs A through the capture session,
  pipeline, handoff and planner.

Wired into `LootScanCaptureHandoff` (the loot grid on `CaptureAnalysis.Grid`, the backpack on
`CaptureAnalysis.CarriedGrid`) and
`StashScanCaptureHandoff` (see `docs/STASH_SCAN.md`). `LootScanRecommendationSource` hands each
named item to the decision engine with what the companion holds: prices and the flea fee, the
profile's pins and item rules, outstanding quest and hideout needs, and the raid phase and risk.
What is decided from that, and what is still unread, is in `docs/LOOT_SCAN.md`. The older
benchmark (`GridRecognitionCorpusBenchmarkTests`) still runs the builder over a local,
never-committed screenshot corpus and scores it against optional
`<screenshot>.expected.json` sidecars, which is where the first real screenshots should go.

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
result owned by the OCR path. It is not #272's `run-plan.v1`/`predictions.v1` interchange
(`docs/research/RECOGNITION_CORPUS.md`), which forbids OCR strings and per-sample output and is
the only form in which recognition results reach the scorer; a probe report is never scored. Its
nullable confidence is intentional. Probe reports are local diagnostic material and
must not be committed or uploaded as CI artifacts.

The anchors and rendered scenes remain synthetic and English-only. Live validation across
EFT themes, localization, HDR, ultrawide layouts, and UI revisions is still outstanding.
No output from this subsystem should be described as a live detection or gameplay-state guarantee.

## Capture sessions (v2 checkpoint)

Capture sessions are an Application-owned intake and review boundary for pixels the player
explicitly supplied, pasted, selected, or caused the game to write. An armed intent is claimed
atomically when intake accepts the next submission; a full bounded queue rejects visibly instead
of retaining waiting writers. The single
preparation reader preserves intake order, while independent review waits do not occupy that
reader or a runtime-supervisor CPU slot. Every decoded buffer has one `CapturePixelLease` owner,
accounts its exact owned byte segment against the configured pixel budget, and is fully zeroed on acceptance, duplicate rejection,
cancellation, timeout, analysis failure, retry, and shutdown.

Artifacts preserve delivery and source kind, capture/submission UTC, correlation and batch IDs,
the frozen workspace context, canonical `ProfileContext` when supplied, confidence, review
correction, decode revision, and evidence provenance. Content digests are process-local dedupe keys only: they are not
published or persisted, are bounded and expired, and are cleared when review requests a retry.
`UseArmedIntent` is carried to consumers as an explicit decision and effective intent; the
detector result remains available as evidence rather than being silently rewritten. Responses are
revision-bound, and unavailable, ambiguous, unknown, expired, cancelled, or failed captures record
a typed no-change result without entering domain handoff. Valid results discard pixels before the
bounded durable handoff; only its positive acknowledgement publishes an accepted result.

This checkpoint does **not** register capture sessions in production. `SupervisedCaptureWorkScheduler`
is the adapter to the #268 runtime supervisor, but App composition issue #294 still owns creating
the concrete recognition pipeline, review UI, durable accepted-result handoff, and canonical active
`ProfileContext` provider. Until that composition lands, `RaidObservationService` receives no
`ICaptureSessionService`, and the established `ScanUseCase`/HUD path remains authoritative. The
bounded screenshot decoder from OCR PR #333 is also an integration dependency; its deadline,
pixel cap, encoded-buffer lifetime, and per-attempt failure contract must survive reconciliation.

### What the player sees, and what a capture is read as (#287)

`V2ShellCaptureBridge` is the composition-owned adapter between the coordinator and the shell. Two
things it used to leave undone:

**Every review resolved itself.** The bridge preferred the detected context and fell back to the
armed intent, always, so the disagreement the coordinator reports on
`CaptureReviewRequest.HasIntentDisagreement` never reached anybody. The shell had rendered the
attention panel for it since #266 and nothing ever filled it. It now pauses on exactly two cases —
the recognizer read a different screen than the one armed (Skip / Analyse as armed / Analyse as
detected), and it could not place the screen at all (Skip / Analyse as armed / Retry). Agreement
still resolves silently. The unplaceable case deliberately offers *as armed* rather than *as
selected*: the frame was taken under the intent that was armed when the shutter fired, and the
coordinator has no action that re-analyses one artifact as a different intent.

**Nothing read a single item.** `CaptureAnalysis` carried a context and a confidence but no
identity, and the pixels are released the moment analysis returns, so what a frame showed could
not be recovered afterwards. It now carries `Identified`: the ranked catalog matches, pixel-free.
`CaptureRecognitionPipeline` fills them from the lines the OCR coordinator already read, through
the same `OcrItemCandidates` ranking `RecognitionService` uses — one read, one answer, rather than
a second recognizer over the same frame. Only item-shaped screens are resolved; a stash grid's
hundreds of lines are a lattice, and the reconstruction is the right reading of that frame.

`IntelCaptureHandoff` takes the intents that reached no workspace before (Auto, Ammo, Keys, Quest
items, Flea — all of which returned Accepted and produced nothing) and publishes the item. The
bridge shows it as a review naming its alternates and their confidence, and opens that item's Intel
page. A capture that identified nothing publishes nothing rather than opening Intel on a guess.

The capture context is also no longer empty: active workspace, profile id, map, plan, selected
entity and prior scan come from the router's own navigation context, which the shell already keeps
current. The profile's stable id, never its display name — a context that named the player would
put their handle into every report.

Manual picker, drop and clipboard-file intake accepts up to 32 pictures as one ordered batch;
the Capture panel shows each picture's status, and cancelling stops the remaining session work.

To see either state without a game: `tools/V2RenderPreview --capture-demo
disagreement|unknown|identified`.

Reviewed stash frames cross a separate pixel-free assembly boundary described in
`docs/STASH_SCAN.md` and ADR 0018. That boundary deduplicates exact content, stitches only unique
evidence-backed overlap, retains unresolved origins and failed ordinal gaps, and never invents
closed nested-container contents. It does not change this checkpoint's composition ownership.
- Each game screenshot is recognised by the always-on scan and by an automatic V2 capture session; the shared `OcrCoordinator` reads identical pixels once for both (`SharesIdenticalFrames`, #453).
