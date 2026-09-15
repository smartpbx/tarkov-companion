# ADR 0013: Contextual capture sessions own transient review state

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

`CaptureSessionCoordinator` owns a bounded intake channel, a bounded/pruned session and artifact
history, process-local dedupe, review decisions, and decoded-pixel leases. Intent binding and
provisional session creation happen atomically only when the channel accepts the item. A full
channel refuses intake with a typed result; it never retains an unbounded population of waiting
writers. Preparation remains a single ordered reader admitted through `ICaptureWorkScheduler`;
reaching review transfers the lease to an independent review task so a human pause cannot block
later intake. Production scheduling uses `SupervisedCaptureWorkScheduler`, an adapter to the #268
process-wide supervisor.

Each progress append is validated against a proposed immutable snapshot before mutable history is
committed. Session cancellation is a durable flag and linked token, so a decoder or recognizer
that ignores cancellation and returns late can only be cleaned up; it cannot append after a
terminal stage. A single `CapturePixelLease` owns the exact decoded managed buffer, accounts every
byte before analysis, and cryptographically clears every byte on every exit. Encoded-file limits
and per-attempt decode deadlines belong to the authoritative screenshot loader. A bounded session
attempt policy retries a watched file when that loader reports a transient incomplete decode.

Review always records the decision and decode revision as a correction; an answer for an older
revision cannot resolve a newer review. `UseArmedIntent` publishes the user-selected effective
intent without erasing the detector output. Retry removes that artifact's dedupe entry, re-arms
the same session with a fresh capped expiry, and permits the same content again. Unknown,
unavailable, ambiguous, expired, cancelled, and failed results are typed no-change outcomes and
never enter domain handoff. Valid reviewed results release their pixels before crossing the
pixel-free `ICaptureResultHandoff` boundary. Only a bounded, durable acceptance acknowledgement
produces `Accepted`; refusal or an unknown acknowledgement is retained as a failed handoff.
Consumers use the artifact id as their idempotency key. An unknown acknowledgement keeps the
content-dedupe entry until its normal expiry so the same pixels cannot immediately create a second
commit attempt whose first outcome is still uncertain.

Accepted artifacts preserve source and delivery kinds, capture and submission UTC,
correlation/batch/device context, canonical `ProfileContext` when available, evidence provenance,
confidence, and revision. No pixels, source paths, or private content digests enter snapshots or
persistence.

The Windows screenshot watcher uses bounded polling state. It ignores attribute-only changes,
accepts same-path byte replacements after settling, and emits a typed availability failure when
the configured root disappears or becomes unreadable. The observation owner immediately stops
claiming screenshot readiness and rediscovers that source without stopping a healthy log watcher.
Filename position evidence proceeds before the bounded scan backlog. The established `ScanUseCase`
and HUD consumers remain active while capture composition is incomplete.

## Composition boundary

This ADR does not claim a production end-to-end capture feature. App composition issue #294 owns
registering the coordinator, concrete `ICaptureSessionPipeline`, review presentation, durable
`ICaptureResultHandoff` consumer, and canonical active `ProfileContext` provider. OCR PR #333 is authoritative
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
