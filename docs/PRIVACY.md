# Privacy

This companion is external and read-only toward Escape from Tarkov. It never accesses game memory, injects or hooks code, inspects EFT network traffic, creates gameplay input, automates gameplay, detects enemies, renders ESP/radar, or draws an in-game overlay. Diagnostics have the same boundary.

## Current data paths

| Material | Current handling | Privacy fact |
| --- | --- | --- |
| `SanitizedDiagnosticEvent` | In-memory primitive only | Closed typed record with no string properties; not persisted or exported yet. |
| Crash recovery primitive | In-memory only | At most 12 copied sanitized events and 12 run boundaries; #270 owns any durable recovery storage. |
| Desktop support bundle | Created on demand by the desktop | Closed, bounded projection containing fixed categories, booleans, capped counts, numeric build/platform facts, and screenshot-name compatibility counts. It does not open the log or render names, paths, coordinates, credentials, OCR/pixels, exception bodies, or other free-form runtime text. |
| Problem report | Relay reports directory | The ordinary desktop sends that exact closed bundle, but the endpoint accepts any caller-supplied body and retains it verbatim. A validated public issue is limited to reference, size, and received UTC. |
| Public relay `/health` | Public endpoint | Includes aggregate room/member counts and start time today, in addition to status/build fields. |
| `RelayReadiness` | No route or consumer | The model exists but does not expose an authenticated endpoint yet. |

The closed event model and desktop support-bundle projection both exclude free text, paths, screenshots, credentials, names, coordinates, raw log lines, and exception bodies. Hostile complete-payload fixtures enforce the desktop boundary. This does not make the report transport closed: **Report a problem** still sends without a separate confirmation preview, and the relay accepts arbitrary bodies from network callers. #281 owns the remaining preview experience; #310 owns relay validation, persistence, retention, and lifecycle. Report bodies must not be copied into public issues.

## Controls that are not wired yet

`DiagnosticRuntimeControls` can parse `TARKOV_COMPANION_DIAGNOSTIC_LOG_LEVEL` and the exact request value `TARKOV_COMPANION_INTERNAL_TELEMETRY=enabled`, but the latter is not user consent and composition does not read either value yet. No telemetry collector is wired by this branch; any future exporter must independently require reviewed in-app consent.

`DiagnosticTokenSet` can validate one current token and up to two previous tokens with a strict UTC timestamp and a maximum 24-hour overlap. The live developer command channel does not use that validator and accepts the environment value as one literal token. Consequently, setting `TARKOV_COMPANION_DIAGNOSTIC_TOKEN` to `new,old@...` would not rotate the live channel; do not do so until #294 changes that consumer.

## Monitoring and public issues

The relay-watch classifier and workflow avoid printing the relay URL and health-response body. For a report, the public issue is constrained to a 12-hex reference, byte count, and received UTC, without the relay address, endpoint, authorization command, or body. GitHub search supplies only a bounded candidate set; local exact-title matching decides whether to comment, skip, or create. Health incidents deduplicate among open issues, while an open or closed report reference is never filed again.

Today `/reports` returns a timestamp-prefixed stored name where the issue path requires the original 12-hex reference. The closed-schema validator fails the entire list before any issue write. #310 must align the list and read contracts before automated report pickup is operational.

The foundation still awaits #270 persistence and #294 composition. It does not authorize a second store, automatic telemetry, token rotation, or a readiness endpoint, and it does not complete #281.
