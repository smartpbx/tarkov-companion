# Runbook exercises

Exercises use fixtures and test rooms only; they never use production player data or record secrets, names, coordinates, paths, screenshots, or report bodies.

| Exercise | Evidence | Result | Cadence |
| --- | --- | --- | --- |
| Stale relay alert | `bash deploy/group-server/monitoring/test-relay-watch.sh` | Returns `stale` with a sanitized update-age reason. | Every monitoring change (CI) |
| Token rotation/expiry | `SanitizedDiagnosticsTests.RotatedTokenIsAcceptedOnlyUntilItsExplicitExpiry` | Current token works; previous token ends at its UTC expiry. | Every diagnostics change (CI) |
| Privacy boundary | `SanitizedDiagnosticsTests.StructuredEventHasNoFreeTextOrSensitiveFieldSurface` | No message, path, name, coordinate, or report-body field exists. | Every diagnostics change (CI) |

Quarterly private operator exercises rehearse relay backup/restore, updater rollback, clock repair, and privacy response in a non-production environment. Record only exercise date, operator, fixture id, and pass/fail outcome.
