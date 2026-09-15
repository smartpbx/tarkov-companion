# Privacy

This companion is external and read-only toward Escape from Tarkov. It never accesses game memory, injects or hooks code, inspects EFT network traffic, creates gameplay input, automates gameplay, detects enemies, renders ESP/radar, or draws an in-game overlay. Diagnostics have the same boundary.

## Current data paths

| Material | Current handling | Privacy fact |
| --- | --- | --- |
| `SanitizedDiagnosticEvent` | In-memory primitive only | Closed typed record with no string properties; not persisted or exported yet. |
| Crash recovery primitive | In-memory only | At most 12 copied sanitized events; #270 owns any durable recovery storage. |
| Existing support bundle | Created on demand by the desktop | Can include a redacted tail of the companion log and masked screenshot-name shapes; it is not the closed event record. |
| Problem report | Relay reports directory | The body remains on the relay; a public issue can contain its reference, size, and received UTC. |
| Public relay `/health` | Public endpoint | Includes aggregate room/member counts and start time today, in addition to status/build fields. |
| `RelayReadiness` | No route or consumer | The model exists but does not expose an authenticated endpoint yet. |

The closed event model deliberately excludes free text, paths, screenshots, credentials, names, coordinates, raw log lines, and report bodies. That promise applies to that new type—not to the pre-existing support bundle or report transport. Those older paths remain subject to their own redaction rules and must not be copied into public issues.

## Controls that are not wired yet

`DiagnosticRuntimeControls` can parse `TARKOV_COMPANION_DIAGNOSTIC_LOG_LEVEL` and the exact opt-in value `TARKOV_COMPANION_INTERNAL_TELEMETRY=enabled`, but composition does not read either value yet. No telemetry collector is wired by this branch.

`DiagnosticTokenSet` can validate one current token and up to two previous tokens with a strict UTC timestamp and a maximum 24-hour overlap. The live developer command channel does not use that validator and accepts the environment value as one literal token. Consequently, setting `TARKOV_COMPANION_DIAGNOSTIC_TOKEN` to `new,old@...` would not rotate the live channel; do not do so until #294 changes that consumer.

## Monitoring and public issues

The relay-watch classifier and workflow avoid printing the relay URL and health-response body. For a report, the public issue intentionally includes its safe reference, byte count, and received UTC, but not the relay address, report endpoint, authorization command, or report body. The issue helper deduplicates by an open exact-title search: health incidents are commented and report issues are skipped. Its fixture verifies those branches and creation; that is not evidence of a live issue or a guarantee about unrelated titles.

The foundation awaits final runtime/main integration, #270 persistence, and #294 composition. It does not authorize a second store, automatic telemetry, token rotation, or a readiness endpoint.
