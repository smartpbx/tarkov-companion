# Agent I — production recognition and scan pipeline remediation

Work in a fresh Orca worktree from current `origin/main`. Read `AGENTS.md`, the autonomous handoff, `docs/reviews/RECOGNITION_REVIEW.md`, and the recognition sections of `docs/reviews/ARCHITECTURE_REVIEW.md` first. Commit the completed work and report the commit hash and verification evidence.

## Goal

Deliver a real local pixel-to-result recognition path and an Application-layer scan use case. Scripted OCR fixtures may remain for post-OCR unit tests, but must not be constructible as the production provider and must not be used to claim screenshot accuracy.

## Ownership

- recognition-specific contracts/domain changes in Core
- scan orchestration under `src/TarkovCompanion.Application/Services/Recognition/**`
- OCR, context, item/extract/container/flea/icon implementations under `src/TarkovCompanion.Infrastructure/Recognition/**`
- Windows recognition provider code, if selected, under `src/TarkovCompanion.Platform.Windows/**`
- recognition fixtures/tests/docs and dependency/license notes directly caused by the OCR provider

Do not edit the App composition root or general page ViewModels. Expose a small registration/factory extension or explicit constructors the integration owner can consume. Do not touch tarkov.dev map UI/catalog code.

## Required work

1. Select and implement one production, offline `IOcrEngine` suitable for packaged Windows x64. Perform and document package/native/traineddata license and redistribution review before adding it. Provider absence must be explicit in availability/self-test state; never silently substitute `FixtureOcrEngine`.
2. Add an Application-layer scan use case that performs capture -> contextual OCR/recognition -> context dispatch -> recommendations/result publication. It must support single item, extract list using current-map canonical extracts, mixed container, and flea rows. Return structured evidence/confidence/timestamps and an honest partial/unavailable result.
3. Build canonical resolver data from the item repository instead of caller-authored production lists. Persist scan event metadata to a narrow repository contract; never persist captured pixels by default.
4. Fix extract status semantics. Preserve `Active | Closed | Pending | Unknown`; closed/pending extracts must not enter the active set. Add runner-up ambiguity handling.
5. Extend the container result model with unresolved/ambiguous cells and a partial-total indicator. Implement practical grid/occupied-cell detection for the deterministic simulator scenes, quantity parsing, and orchestration. Low-confidence cells remain excluded from precise totals and visibly flagged in the result.
6. Make contextual OCR regions anchor-relative and fall back safely to full-frame candidates. Data-drive/provenance-label anchor terms.
7. Single-source recognition thresholds in Core and select the highest-confidence eligible candidate independent of input ordering. Define engine confidence normalization.
8. Repair icon fallback or explicitly disable it as unavailable. If implemented: crop an item ROI, use averaged dHash, populate fingerprints from licensed runtime-cached item icons, calibrate a hard negative cutoff, and add unrelated-image negative tests. Raw hash similarity must not be presented as probability.
9. Separate the scripted post-OCR fixtures from rendered screenshot accuracy tests. Add actual rendered pixel fixtures covering at minimum item, similar-name ambiguity, extract status, mixed container, and flea rows across representative resolutions/scales/noise. Run the production OCR engine when available and report skips honestly when native assets are unavailable; do not report scripted text as OCR accuracy.
10. Add a performance test/measurement for a realistic canonical catalog and keep scan latency bounded.

## Constraints

- Everything local; no cloud OCR or image upload.
- No prohibited game interaction, capture persistence, or fabricated confidence.
- No new dependency without compatible license/provenance documented.
- Keep platform-specific dependencies out of Core/Application.
- Tests must assert behavior, including negatives and ambiguity, not implementation literals.

## Acceptance

- At least one packaged-Windows-capable production OCR provider reads pixels.
- A capture can flow through the scan use case to a canonical item/extract/container/flea result.
- Closed extracts and ambiguous container cells remain honest.
- Fixture docs distinguish post-OCR logic from measured pixel OCR.
- Build has zero warnings; full tests pass.
