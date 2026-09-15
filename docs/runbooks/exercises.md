# Runbook exercises

Exercises use fixtures and test rooms only; they never use production player data or record secrets, names, coordinates, paths, screenshots, or report bodies.

| Exercise | Evidence | Result | Cadence |
| --- | --- | --- | --- |
| Relay watch classifier and issue control | `bash deploy/group-server/monitoring/test-relay-watch.sh` | Covers quiet ready and abbreviated SHAs; updater and publication grace/failure; non-main, ahead, diverged, unknown, invalid, failed-run, and digest-tamper evidence; exact issue matching; immutable closed report references; and hostile whole-list rejection before writes. | Every monitoring change (CI) |
| Token validation primitive | `SanitizedDiagnosticsTests.TokensRequireUtcBoundedExpiryAndSafeShape` | Validates a bounded UTC overlap; it does not prove live token rotation, which is not wired. | Every diagnostics change (CI) |
| Privacy boundary | `SanitizedDiagnosticsTests.StructuredEventHasAnExactClosedNonStringSurface` | The new closed event has exactly its approved non-string fields. | Every diagnostics change (CI) |

Quarterly private operator exercises rehearse relay backup/restore, updater rollback, clock repair, and privacy response in a non-production environment. A restore exercise must prove that `INSTALLED_SHA256`, `REFUSED_SHA256`, and `UPDATE_NOW` came from the current host/install process rather than the backup. Record only exercise date, operator, fixture id, and pass/fail outcome.
