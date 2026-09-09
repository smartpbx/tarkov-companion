# Recognition pipeline

The recognition subsystem consumes only caller-supplied `CapturedImage` buffers. It does not capture windows, persist frames, upload screenshots, inspect game memory, or make gameplay inputs. A frame remains in memory for the duration of the call and ownership stays with the caller.

## Pipeline

`OcrCoordinator` runs a provider-neutral full-frame OCR pass, classifies the visible scene using normalized OCR anchors and scale-independent regions, and runs a context-specific OCR pass only after a context is established. `FixtureOcrEngine` provides deterministic in-memory responses for tests; production OCR providers implement the existing Core `IOcrEngine` contract.

Supported contexts are single-item inspection, containers, the extract list, visible flea listings, and unknown. An unknown or tied scene returns no recognized state and does not invoke icon fallback. OCR text is Unicode-normalized and repairs common letter/number confusions before Damerau-Levenshtein and token similarity are compared against caller-supplied canonical item names and aliases.

Confidence handling is explicit:

- `>= 0.90`: auto-selected
- `>= 0.70` and `< 0.90`: ambiguous and must be confirmed
- `>= 0.45` and `< 0.70`: candidate only
- `< 0.45`: no match

Each candidate retains evidence and bounds. `RecognitionResult` retains the captured UTC timestamp. Extract matches are restricted to the current map's canonical extracts and include OCR engine, map, and observed UTC time in their source string because the current Core extract result has no timestamp field.

## Fallbacks and beta parsers

`SkiaPerceptualIconMatcher` computes a 64-bit difference hash from an in-memory Skia bitmap. When a deployment omits Skia's native runtime, the same fixed sampling algorithm falls back to the managed captured-pixel reader. It is a lightweight fallback for weak OCR, not a semantic classifier. References must be built from licensed or user-provided icon pixels by the caller.

`ContainerGridSegmenter` samples normalized grid cells and marks cells occupied using luminance deviation/variation. `ContainerScanAnalyzer` associates recognized bounds with occupied cells, excludes candidates below `0.70`, aggregates duplicates and quantities, totals caller-supplied values, and orders drop-first IDs by ascending value per occupied slot. This beta ranking is informational inventory triage only; it contains no quest, hideout, combat, or recommendation rules.

`FleaListingParser` parses only OCR rows with a visible rouble marker (`₽`, `RUB`, or `ROUBLES`) and optional `x`/`qty` quantities. Results are informational; the subsystem never buys, sells, clicks, or types.

## Fixtures and limitations

`fixtures/recognition/synthetic-scenes.json` covers 1920×1080, 2560×1440, and 3840×2160 scenes at 100%, 125%, and 150% UI scale with deterministic synthetic noise. The fixtures exercise every supported context and the unknown safeguard without storing captured screen images.

The detector depends on visible English UI anchors and is not yet a production-trained vision model. Perceptual hashes can confuse visually similar icons, container segmentation expects a caller-supplied grid region and dimensions, multi-line flea rows are not joined, and real-game validation across themes, localization, HDR, ultrawide layouts, and future UI changes remains outstanding. No result should be described as a live detection or gameplay-state guarantee.
