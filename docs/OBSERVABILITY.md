# Observability

Tarkov Companion remains external and read-only toward Escape from Tarkov. Observability does not access game memory, inject or hook code, inspect EFT traffic, generate gameplay input, detect players, provide ESP/radar, or render an in-game overlay.

## What is implemented now

This branch provides unpersisted, closed diagnostic primitives in `TarkovCompanion.Infrastructure/Diagnostics`. `SanitizedDiagnosticEvent` contains only an event kind, random process-run and operation `CorrelationId` values, UTC time, enumerated outcome and optional canonical runtime failure kind, a duration capped at five minutes, and a bounded attempt number. It has no string properties. A failure kind is required exactly for a failed outcome.

`CrashRecoveryState` copies and retains no more than twelve events and twelve explicit run boundaries. A new run marks a preceding unterminated run as unclean; a clean exit is recorded separately. Unrecovered failures are matched by both run and operation correlation, so recovery of one operation cannot erase another operation's failure. An unclean run may truthfully have no recorded failure because a terminated process cannot always emit one. #270 remains the sole owner of any storage, schema, retention, export, deletion, or restore path.

`DiagnosticRuntimeControls.FromEnvironment` and `DiagnosticTokenSet` are validation primitives, not startup integrations. Nothing currently reads `TARKOV_COMPANION_DIAGNOSTIC_LOG_LEVEL` or `TARKOV_COMPANION_INTERNAL_TELEMETRY` during composition. Likewise, the existing developer diagnostic command channel still reads `TARKOV_COMPANION_DIAGNOSTIC_TOKEN` as one literal token; it does not consume `DiagnosticTokenSet`, so token rotation is not implemented yet.

The current public relay `/health` response is not liveness-only. It publishes status, protocol, version, commit, start time, and aggregate `rooms` and `members` counts. `RelayReadiness` is a pure, unconsumed model: no readiness route or control consumer is wired. When #294 composes it, operational probes must be relay-owned, updater state must come from a verified updater check, and rejection counters must cover the exact preceding five minutes. Up to three rate-limit or rejected-input observations are normal operational signals; the fourth elevates the relevant signal without making a correctly rejecting relay unready. Three consecutive failures degrade readiness, as does more than 90 minutes since the last successful updater check.

Existing support bundles and problem reports are separate, older paths. A support bundle can include a redacted tail of the companion's own log and masked screenshot-name shapes. It is not a `SanitizedDiagnosticEvent` and this branch does not claim that it is a closed diagnostic record. The existing report flow remains described in `docs/OPERATIONS.md`; do not treat its reports as safe to paste into a public issue.

## Relay watch

`relay-watch.yml` runs the fixture job with read-only contents permission, including for pull requests; issue-writing permission is limited to the default-branch, non-PR watch job. Pull-request fixtures and live watches use separate concurrency lanes and both have timeouts. The classifier accepts the bounded health shape without logging its URL or body. It binds `dev/update.json` to GitHub's release-asset digest and publication time, then verifies the manifest's branch, commit, run id, and successful `windows-verify.yml` push run before comparing the relay. A relay on the verified full or abbreviated SHA is ready regardless of commit age. A relay behind that publication is `updater-delayed` for 45 minutes and `updater-stale` afterward. A default-branch commit ahead of the verified feed is separately `publication-delayed` or `publication-failed`; non-main, ahead, diverged, unknown, and invalid evidence have distinct fixed classifications.

The issue helper treats GitHub search as a bounded candidate set and applies exact title equality locally. It comments on one open health incident and refuses ambiguous duplicate titles. Report metadata is validated as one complete, closed-schema, bounded list before any issue write; an existing open or closed report title is immutable and skipped. Report issues contain only a 12-hex reference, byte count, and received time—not a relay URL, endpoint, or body.

The current relay list route returns the timestamped stored filename as `reference`, while its read route accepts the original 12-hex reference. The validator therefore rejects the live listing and writes no issues until #310 aligns those contracts. This is a downstream #310 handoff, not evidence that report retrieval works and not a reason to weaken the validator.

## Deferred integration

This foundation is not a claim that #281 is complete. After the final rebase onto `main`, #294 owns composition: it must register runtime controls, consume validated tokens only after the command channel is changed to support them, wire an authenticated readiness consumer, and decide how public-health fields are minimized. #270 owns the one durable diagnostic store. #279 still needs its own fixture-safe Windows page-readiness signal; this branch neither implements nor assigns it.

Use the runbooks for operational response. Public evidence may contain UTC, a random runtime correlation id, enumerated status/failure, and a safe report reference—never a token, report body, screenshot, raw log, name, coordinate, or full path.
