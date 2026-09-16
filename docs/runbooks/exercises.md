# Runbook exercises

Exercises use fixtures and test rooms only; they never use production player data or record secrets, names, coordinates, paths, screenshots, or report bodies.

| Exercise | Evidence | Result | Cadence |
| --- | --- | --- | --- |
| Relay watch classifier and issue control | `bash deploy/group-server/monitoring/test-relay-watch.sh` | Covers current, older, and abbreviated default-branch SHAs; ahead, diverged, unknown, and invalid health evidence; exact issue matching; immutable closed report references; hostile whole-list rejection before writes; and refusal to consult the retired public `dev` release. | Every monitoring change (CI) |
| Token validation primitive | `SanitizedDiagnosticsTests.TokensRequireUtcBoundedExpiryAndSafeShape` | Validates a bounded UTC overlap; it does not prove live token rotation, which is not wired. | Every diagnostics change (CI) |
| Privacy boundary | `SanitizedDiagnosticsTests.StructuredEventHasAnExactClosedNonStringSurface` | The new closed event has exactly its approved non-string fields. | Every diagnostics change (CI) |

Quarterly private operator exercises rehearse relay backup/restore, updater rollback, clock repair, and privacy response in a non-production environment. A restore exercise must prove that `UPDATE_NOW` was not restored, that derived status mirrors were not treated as authority, and that authenticated updater history was either preserved as a complete protected host-state backup or re-established through the documented signed-feed bootstrap. Record only exercise date, operator, fixture id, and pass/fail outcome.
