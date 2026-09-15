# Privacy

This companion is external and read-only toward Escape from Tarkov. It never accesses game memory, injects or hooks code, inspects EFT network traffic, creates gameplay input, automates gameplay, detects enemies, renders ESP/radar, or draws an in-game overlay. Diagnostics have the same boundary.

## Current data paths

| Material | Current handling | Privacy fact |
| --- | --- | --- |
| `SanitizedDiagnosticEvent` | In-memory primitive only | Closed typed record with no string properties; not persisted or exported yet. |
| Crash recovery primitive | In-memory only | At most 12 copied sanitized events and 12 run boundaries; #270 owns any durable recovery storage. |
| Existing support bundle | Created on demand by the desktop | Includes selected runtime details, masked screenshot-name samples, and a redaction attempt over an app-log tail. Complete-bundle absence of paths and coordinates is not yet proven. |
| Problem report | Relay reports directory | The submitted body is retained verbatim and can contain sensitive diagnostic material. A validated public issue is limited to reference, size, and received UTC. |
| Public relay `/health` | Public endpoint | Includes aggregate room/member counts and start time today, in addition to status/build fields. |
| `RelayReadiness` | No route or consumer | The model exists but does not expose an authenticated endpoint yet. |

The closed event model deliberately excludes free text, paths, screenshots, credentials, names, coordinates, raw log lines, and report bodies. That promise applies to that new type—not to the pre-existing support bundle or report transport. The current complete bundle can still retain raw roots, screenshot filenames, or coordinates through fields the narrow redactor does not cover. That release-blocking work remains with #281/#310; report bodies must not be copied into public issues.

## Controls that are not wired yet

`DiagnosticRuntimeControls` can parse `TARKOV_COMPANION_DIAGNOSTIC_LOG_LEVEL` and the exact request value `TARKOV_COMPANION_INTERNAL_TELEMETRY=enabled`, but the latter is not user consent and composition does not read either value yet. No telemetry collector is wired by this branch; any future exporter must independently require reviewed in-app consent.

`DiagnosticTokenSet` can validate one current token and up to two previous tokens with a strict UTC timestamp and a maximum 24-hour overlap. The live developer command channel does not use that validator and accepts the environment value as one literal token. Consequently, setting `TARKOV_COMPANION_DIAGNOSTIC_TOKEN` to `new,old@...` would not rotate the live channel; do not do so until #294 changes that consumer.

## Monitoring and public issues

The relay-watch classifier and workflow avoid printing the relay URL and health-response body. For a report, the public issue is constrained to a 12-hex reference, byte count, and received UTC, without the relay address, endpoint, authorization command, or body. GitHub search supplies only a bounded candidate set; local exact-title matching decides whether to comment, skip, or create. Health incidents deduplicate among open issues, while an open or closed report reference is never filed again.

Today `/reports` returns a timestamp-prefixed stored name where the issue path requires the original 12-hex reference. The closed-schema validator fails the entire list before any issue write. #310 must align the list and read contracts before automated report pickup is operational.

The foundation awaits final runtime/main integration, #270 persistence, and #294 composition. It does not authorize a second store, automatic telemetry, token rotation, or a readiness endpoint.
