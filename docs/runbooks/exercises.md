# Runbook exercises

Exercises use fixtures and test rooms only; they never use production player data or record secrets, names, coordinates, paths, screenshots, or report bodies.

| Exercise | Evidence | Result | Cadence |
| --- | --- | --- | --- |
| Relay watch classifier and issue control | `bash deploy/group-server/monitoring/test-relay-watch.sh` | Covers ready current SHA, grace-window mismatch, stale mismatch, old matching SHA, invalid body, and issue comment/create control paths. | Every monitoring change (CI) |
| Token validation primitive | `SanitizedDiagnosticsTests.TokensRequireUtcBoundedExpiryAndSafeShape` | Validates a bounded UTC overlap; it does not prove live token rotation, which is not wired. | Every diagnostics change (CI) |
| Privacy boundary | `SanitizedDiagnosticsTests.StructuredEventHasAnExactClosedNonStringSurface` | The new closed event has exactly its approved non-string fields. | Every diagnostics change (CI) |

Quarterly private operator exercises rehearse relay backup/restore, updater rollback, clock repair, and privacy response in a non-production environment. Record only exercise date, operator, fixture id, and pass/fail outcome.
