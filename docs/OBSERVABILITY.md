# Observability

Tarkov Companion diagnoses its own operation without becoming a second source of game evidence. It remains external and read-only toward Escape from Tarkov: no process-memory access, injection or hooks, EFT traffic inspection, generated gameplay input, enemy tracking, ESP/radar, or in-game overlay.

## Default: local only

Local diagnostics work while telemetry is off. The event model is closed: startup, sync, recognition stage, database operation, map operation, device/relay operation, update operation, failure, and crash recovery. A record carries only its category, random correlation id, UTC time, enumerated outcome/failure class, bounded duration, and bounded attempt number.

It cannot carry screenshots, OCR crops, credentials or keys, names, exact coordinates, report bodies, exception text, or full filesystem paths. Categories are not free-text log messages; downstream redaction cannot prove it found every spelling of a path or secret.

Correlation is a random 128-bit per-operation id, never derived from a person, device, room, profile, filename, or account. It joins desktop, relay, report, update, and CI evidence only when a caller deliberately carries it across that boundary.

## Verbosity and telemetry

`TARKOV_COMPANION_DIAGNOSTIC_LOG_LEVEL` controls local verbosity at runtime. `Trace`, `Debug`, `Information` (default/fallback), `Warning`, and `Error` need no rebuild or debugger. Trace and Debug still use the closed event model, not formatted evidence.

Internal telemetry is off unless `TARKOV_COMPANION_INTERNAL_TELEMETRY=enabled`. This is an explicit opt-in to an inspectable, self-hosted collector; every other value is local-only. Turning it off revokes new export. Before enabling it, the application must show collector address, event preview, retention period, and export/delete controls. No third-party telemetry SDK is permitted.

The primitives live in `TarkovCompanion.Infrastructure/Diagnostics`. Their durable adapter, retention preview, export, and delete operations consume #270's persistence interfaces; this workstream creates no schema, migration, or parallel store. Until that adapter is wired, local-only is the safe default.

## Deferred integration contract

This branch deliberately supplies foundation seams rather than changing shared composition while
the #268 repair is in progress. It is not a release claim. The repaired-runtime transplant and
#294 composition must register `DiagnosticRuntimeControls`, read the log-level and explicit
telemetry environment values at startup, route `DiagnosticTokenSet` into the diagnostic command
channel, emit only `SanitizedDiagnosticEvent`, and bind `RelayReadiness` to an authenticated admin
route while reducing public `/health` to its liveness shape.

#270 supplies the only durable adapter: it accepts sanitized events and bounded
`CrashRecoveryState`, provides retention preview/export/delete, and returns an explicit
unavailable/error outcome on storage failure. It owns all schema, migrations, atomicity, retention
execution, and restore behavior. No caller may persist raw logging text as a substitute.

## Retention and recovery

The diagnostic preview contains event count, time range, categories, correlation ids, and telemetry state — never report bodies or raw payloads. Export uses the same closed record; delete reports success or storage failure honestly.

Crash recovery retains at most twelve sanitized event records. The next startup may state that the preceding run failed, with a correlation id and failure class, but never exception text or a stack trace. #270 owns durable recovery storage; failed storage must present `unavailable`, not a fabricated clean shutdown.

## Relay signals

Public `/health` is liveness only: a short status, protocol number, build version, and public commit identifier. Storage/disk/clock/latency/failure/rate-limit/rejected-input detail, activity counts, and report references are authenticated readiness data, not public health output.

Admin readiness evaluates storage, disk headroom, clock drift, known build, update age, latency, consecutive failures, rate-limit rejections, and rejected input. It returns check identities and status only — no paths, rooms, keys, names, positions, or report bodies. The pure `RelayReadiness` model is wired into shared `Program.cs` composition by the integration owner.

The hourly workflow checks out full history, validates the reported commit, and uses `deploy/group-server/monitoring/relay-watch.sh`. The classifier prints neither relay URL nor health body. Its fixture proves that a stale deployment raises the single existing incident rather than silently passing or creating hourly duplicates.

## Windows page-readiness handoff (#279)

The #279 Windows gallery must wait for a fixture-safe semantic readiness handoff, never a fixed delay or a screenshot. Each gallery scenario supplies the expected page identity and requires the rendered page to report that same stable semantic identity after navigation; a title, route text, or captured image is not sufficient proof that the intended page is current.

The handoff reports only enumerated state: required data is `ready`, `unavailable`, or `error`; a map page also reports required tile data as `ready`, `unavailable`, or `not-applicable`; and the navigation/visual state is `settled` only after the page has finished its declared transition and has no pending page animation. #279 must reject a wrong identity, loading data, an unsettled animation, missing tiles where tiles are required, or an undeclared error rather than photographing a transient state.

These states come from synthetic fixture data and existing UI/test seams. They carry no screenshots, private paths, player names, coordinates, report bodies, or live-game claim; gallery capture remains evidence after readiness, not the signal that establishes readiness.

Use [`docs/runbooks/`](runbooks/) for storage, clock, update, rotation, backup/restore, and privacy incidents. Record UTC, random correlation id, enumerated failure class, and outcome only; never attach screenshots, logs, credentials, names, report bodies, coordinates, or full paths to a public issue.
