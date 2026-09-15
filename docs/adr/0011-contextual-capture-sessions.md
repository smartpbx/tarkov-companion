# ADR 0011: Contextual capture sessions own transient review state

Status: Accepted as an uncomposed v2 checkpoint

Date: 2026-09-15

## Context

The companion needs one explainable lifecycle for screenshots the player explicitly supplies
through desktop, paired-device, paste, drop, picker, watched-file, external-capture, and batch
paths. An intent such as “scan stash” must attach to the capture that arrived next, not whichever
capture happens to finish decoding next. Recognition may be slow or unavailable, reviewers may
pause, retry, correct, or cancel, and decoded screen pixels must never become durable application
state.

The application remains external and read-only relative to Escape from Tarkov. It reads ordinary
files or visible pixels only; it does not read game memory, inject or hook, inspect network
traffic, generate input, automate inventory or combat, track other players, or render an in-game
overlay. Recognition is evidence from a captured instant, never a live detection.

## Decision

`CaptureSessionCoordinator` owns a bounded intake channel, a bounded/pruned session history,
process-local dedupe, review decisions, and decoded-pixel leases. Intent binding and provisional
session creation happen atomically at intake before any queue wait. Preparation remains a single
ordered reader admitted through `ICaptureWorkScheduler`; reaching review transfers the lease to
an independent review task so a human pause cannot block later intake. Production scheduling uses
`SupervisedCaptureWorkScheduler`, an adapter to the #268 process-wide supervisor.

Each progress append is validated against a proposed immutable snapshot before mutable history is
committed. Session cancellation is a durable flag and linked token, so a decoder or recognizer
that ignores cancellation and returns late can only be cleaned up; it cannot append after a
terminal stage. A single `CapturePixelLease` owns each decoded managed buffer, accounts its byte
length before analysis, and cryptographically clears it on every exit. Encoded-file limits and
decode deadlines belong to the authoritative screenshot loader, so the coordinator does not
multiply that loader's retry policy.

Review always records the decision as a correction. `UseArmedIntent` publishes the user-selected
effective intent without erasing the detector output. Retry removes that artifact's dedupe entry,
re-arms the same session with a fresh capped expiry, and permits the same content again. Accepted
artifacts preserve source and delivery kinds, capture and submission UTC, correlation/batch/device
context, canonical `ProfileContext` when available, confidence, and revision. No pixels, source
paths, or private content digests enter snapshots or persistence.

The Windows screenshot watcher uses bounded polling state. It ignores attribute-only changes,
accepts same-path byte replacements after settling, waits through exact-root recreation, and lets
filename position evidence proceed before the bounded scan backlog. The established `ScanUseCase`
and HUD consumers remain active while capture composition is incomplete.

## Composition boundary

This ADR does not claim a production end-to-end capture feature. App composition issue #294 owns
registering the coordinator, concrete `ICaptureSessionPipeline`, review presentation,
`Accepted` consumer, and canonical active `ProfileContext` provider. OCR PR #333 is authoritative
for `SkiaScreenshotImageLoader` decode bounds, deadline, and encoded/native buffer lifetime; the
capture branch deliberately does not replace it. Those dependencies must be merged and the exact
combined head must pass Linux and Windows GitHub Actions before this checkpoint can be called
ready to merge.

## Consequences

- Intake ordering, cancellation, retry, provenance, and memory release are fixture-testable
  without Windows or native OCR.
- Review latency no longer occupies the intake reader, but retained review pixels still consume
  the explicit global budget and therefore provide honest backpressure.
- Dedupe and session history are intentionally process-local and bounded; restart permits the
  same capture again.
- A caller without canonical profile context records no substitute. In particular, the legacy
  runtime `PlayerProfile.Id` is not relabeled as the v2 `ProfileContext`.
- Integration remains blocked on #331, #333, #294 composition, and exact-head CI evidence.
