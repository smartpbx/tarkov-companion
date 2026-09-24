# Observability

Tarkov Companion remains external and read-only toward Escape from Tarkov. Observability does not access game memory, inject or hook code, inspect EFT traffic, generate gameplay input, detect players, provide ESP/radar, or render an in-game overlay.

## What is implemented now

This branch provides unpersisted, closed diagnostic primitives in `TarkovCompanion.Infrastructure/Diagnostics`. `SanitizedDiagnosticEvent` contains only an event kind, random process-run and operation `CorrelationId` values, UTC time, enumerated outcome and optional canonical runtime failure kind, a duration capped at five minutes, and a bounded attempt number. It has no string properties. A failure kind is required exactly for a failed outcome.

`CrashRecoveryState` copies and retains no more than twelve events and twelve explicit run boundaries. A new run marks a preceding unterminated run as unclean; a clean exit is recorded separately. Unrecovered failures are matched by both run and operation correlation, so recovery of one operation cannot erase another operation's failure. An unclean run may truthfully have no recorded failure because a terminated process cannot always emit one. #270 remains the sole owner of any storage, schema, retention, export, deletion, or restore path.

`DiagnosticRuntimeControls.FromEnvironment` and `DiagnosticTokenSet` are validation primitives, not startup integrations. Nothing currently reads `TARKOV_COMPANION_DIAGNOSTIC_LOG_LEVEL` or `TARKOV_COMPANION_INTERNAL_TELEMETRY` during composition. Likewise, the existing developer diagnostic command channel still reads `TARKOV_COMPANION_DIAGNOSTIC_TOKEN` as one literal token; it does not consume `DiagnosticTokenSet`, so token rotation is not implemented yet.

The current public relay `/health` response is not liveness-only. It publishes status, protocol, version, commit, start time, and aggregate `rooms` and `members` counts. `RelayReadiness` is a pure, unconsumed model: no readiness route or control consumer is wired. When #294 composes it, operational probes must be relay-owned, updater state must come from a verified updater check, and rejection counters must cover the exact preceding five minutes. Up to three rate-limit or rejected-input observations are normal operational signals; the fourth elevates the relevant signal without making a correctly rejecting relay unready. Three consecutive failures degrade readiness, as does more than 90 minutes since the last successful updater check.

Support bundles and problem reports remain separate from `SanitizedDiagnosticEvent`. The desktop support bundle is now a closed projection of fixed categories, booleans, capped counts, numeric build/platform facts, and compatibility counts from at most three screenshot names. It never opens the supplied log path and never renders free-form runtime detail, a screenshot name, path, coordinate, credential, identity, OCR text, pixel content, or exception body. The ordinary desktop flow therefore sends the same bounded text that **Copy diagnostics** exposes. Setup > Diagnostics shows the whole report first and sends it only after a separate consent tick; if the relay is unreachable the report is queued in `Config/problem-report-outbox.json` and retried with back-off (at most 5 tries, dropped after 7 days, keyed by a hash so the same text is never sent twice; #314). The relay still accepts and retains arbitrary request bodies; those residuals remain with #281/#310 as described in `docs/OPERATIONS.md`.

## Relay watch

`relay-watch-fixture.yml` runs pull-request fixtures with read-only contents permission; `relay-watch.yml` alone runs the scheduled/default-branch watch and grants issue-writing permission only to that live job. Pull-request fixtures and live watches use separate concurrency lanes and both have timeouts. The classifier accepts the bounded health shape without logging its URL or body. It accepts a full or unambiguous abbreviated commit anywhere on the checked-out default-branch lineage without imposing an age policy; unknown, ahead, diverged, and invalid identities have distinct fixed classifications.

That check is liveness and lineage evidence, not release authority. The retired public `dev` release is not consulted. Only the root updater holds the private-feed credential and separately provisioned Sigstore trust root, so signed ring selection, pause, rollback, authenticated publication age, and installed/refused state remain in its root-owned records and the admin panel. The watch must not infer those facts from `main` or from an unsigned checksum.

The issue helper treats GitHub search as a bounded candidate set and applies exact title equality locally. It comments on one open health incident and refuses ambiguous duplicate titles. Report metadata is validated as one complete, closed-schema, bounded list before any issue write; an existing open or closed report title is immutable and skipped. Report issues contain only a 12-hex reference, byte count, and received time—not a relay URL, endpoint, or body.

The current relay list route returns the timestamped stored filename as `reference`, while its read route accepts the original 12-hex reference. The validator therefore rejects the live listing and writes no issues until #310 aligns those contracts. This is a downstream #310 handoff, not evidence that report retrieval works and not a reason to weaken the validator.

## Deferred integration

This foundation is not a claim that #281 is complete. #294 owns composition: it must register runtime controls, consume validated tokens only after the command channel is changed to support them, wire an authenticated readiness consumer, and decide how public-health fields are minimized. #270 owns the one durable diagnostic store. #279 still needs its own fixture-safe Windows page-readiness signal; this branch neither implements nor assigns it.

Use the runbooks for operational response. Public evidence may contain UTC, a random runtime correlation id, enumerated status/failure, and a safe report reference—never a token, report body, screenshot, raw log, name, coordinate, or full path.
