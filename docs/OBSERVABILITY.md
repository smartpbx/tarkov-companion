# Observability

Tarkov Companion remains external and read-only toward Escape from Tarkov. Observability does not access game memory, inject or hook code, inspect EFT traffic, generate gameplay input, detect players, provide ESP/radar, or render an in-game overlay.

## What is implemented now

This branch provides unpersisted, closed diagnostic primitives in `TarkovCompanion.Infrastructure/Diagnostics`. `SanitizedDiagnosticEvent` contains only an event kind, the runtime's random `CorrelationId`, UTC time, enumerated outcome and canonical runtime failure kind, a duration capped at five minutes, and a bounded attempt number. It has no string properties. `CrashRecoveryState` copies and retains no more than twelve of these events; #270 remains the sole owner of any storage, schema, retention, export, deletion, or restore path.

`DiagnosticRuntimeControls.FromEnvironment` and `DiagnosticTokenSet` are validation primitives, not startup integrations. Nothing currently reads `TARKOV_COMPANION_DIAGNOSTIC_LOG_LEVEL` or `TARKOV_COMPANION_INTERNAL_TELEMETRY` during composition. Likewise, the existing developer diagnostic command channel still reads `TARKOV_COMPANION_DIAGNOSTIC_TOKEN` as one literal token; it does not consume `DiagnosticTokenSet`, so token rotation is not implemented yet.

The current public relay `/health` response is not liveness-only. It publishes status, protocol, version, commit, start time, and aggregate `rooms` and `members` counts. `RelayReadiness` is a pure, unconsumed model: no readiness route or control consumer is wired. When #294 composes it, the rate-limit and rejected-input inputs must be relay-owned counts from a fixed five-minute window, not cumulative totals or values supplied by unauthenticated requests. Its thresholds are three such observations and three consecutive failures; the update-age limit is 90 minutes.

Existing support bundles and problem reports are separate, older paths. A support bundle can include a redacted tail of the companion's own log and masked screenshot-name shapes. It is not a `SanitizedDiagnosticEvent` and this branch does not claim that it is a closed diagnostic record. The existing report flow remains described in `docs/OPERATIONS.md`; do not treat its reports as safe to paste into a public issue.

## Relay watch

`relay-watch.yml` runs the fixture job with read-only contents permission, including for pull requests; issue-writing permission is limited to the non-PR watch job. The job has a timeout. The classifier accepts the minimal health shape without logging its URL or body. A relay that reports the expected full or abbreviated SHA is ready regardless of the commit's age. A mismatch is `updating` for a 45-minute deployment grace window (about eight minutes to publish plus the relay's 30±5 minute update poll), then is stale; invalid health responses fail immediately.

The issue helper searches the open title before acting. It comments on the one existing health incident, skips an already-open report issue, and otherwise creates the issue. Its fixture stubs `gh` to cover those control paths. Report issues retain a reference, size, and received time, but do not include a relay URL, report endpoint, or report body. This is control-flow evidence only; it does not assert that a live issue was opened.

## Deferred integration

This foundation is not a claim that #281 is complete. After the final rebase onto `main`, #294 owns composition: it must register runtime controls, consume validated tokens only after the command channel is changed to support them, wire an authenticated readiness consumer, and decide how public-health fields are minimized. #270 owns the one durable diagnostic store. #279 still needs its own fixture-safe Windows page-readiness signal; this branch neither implements nor assigns it.

Use the runbooks for operational response. Public evidence may contain UTC, a random runtime correlation id, enumerated status/failure, and a safe report reference—never a token, report body, screenshot, raw log, name, coordinate, or full path.
