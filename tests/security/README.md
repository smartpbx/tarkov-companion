# Security threat-model fixtures

These are **illustrative data fixtures for the phase-1 threat model in `docs/security/`**, not an
executable test project. Each file is the concrete input named in one row of
[`docs/security/ABUSE_CASES.md`](../../docs/security/ABUSE_CASES.md) — the "Fixture" column links
back here.

## What these are

Plain JSON files, each with a `_meta` block describing which abuse case it illustrates, and a
`scenario` block holding the concrete request/response/state shape that scenario describes.
Reading one alongside its `ABUSE_CASES.md` row should be self-explanatory without touching any
other file.

## What these are not

- **Not wired into any test runner.** No `.csproj` references this directory, and nothing here
  runs as part of `scripts/test.sh` or CI today. This worktree's task was scoped to
  `docs/security/**` and narrow fixture files under `tests/security/**`, deliberately excluding
  `scripts/`, workflow files, and existing test projects — those belong to other owners, and this
  pass ran no `dotnet` command on this host per `AGENTS.md`'s resource-safety rules.
- **Not proof the described behavior happens.** A fixture describing
  `relay-state-weak-key-brute-force.json` is the *input* an abuse case would send; it is not a
  recorded result of actually sending it. `docs/security/CONTROLS_AND_RESIDUAL_RISK.md` states,
  per finding, whether the corresponding control was reviewed by reading source or verified by an
  actual test run — never conflate the two.
- **Not a substitute for real coverage.** Existing relay behavior is covered at unit level under
  `tests/TarkovCompanion.UnitTests` (`GroupKeyTests`, `GroupMarksTests`, `GroupRoomsTests`, and
  related files). `TarkovCompanion.IntegrationTests` does **not** reference GroupServer; its
  `Simulator` area exercises `TarkovCompanion.EftSimulator`. These fixtures therefore need either
  focused unit assertions or a purpose-built relay integration harness. Until that happens, each
  fixture is documentation with a stable, referenceable shape — nothing more.

## Index

| File | Illustrates (abuse case) |
| --- | --- |
| `fixtures/relay-state-name-collision.json` | ABUSE-RELAY-NAME-COLLISION |
| `fixtures/relay-state-fabricated-freshness.json` | ABUSE-RELAY-STALE-FRESHNESS |
| `fixtures/relay-state-weak-key-brute-force.json` | ABUSE-RELAY-WEAK-KEY-GUESS |
| `fixtures/relay-waypoint-flood.json` | ABUSE-RELAY-WAYPOINT-FLOOD |
| `fixtures/relay-public-endpoint-flood.json` | ABUSE-RELAY-PUBLIC-ENDPOINT-DOS |
| `fixtures/catalog-mirror-tamper-scenario.json` | ABUSE-CATALOG-UPSTREAM-TAMPER |
| `fixtures/admin-report-reference-traversal.json` | ABUSE-ADMIN-REPORT-REFERENCE-TRAVERSAL |

The remaining abuse cases in `docs/security/ABUSE_CASES.md` have no fixture here, usually because
the scenario is a sequencing/timing property (ABUSE-ADMIN-KEY-TIMING), a local or operator trust
event (ABUSE-GROUP-KEY-LOCAL-RECOVERY, ABUSE-RELAY-PLAINTEXT-KEY), or a future review/control
failure (ABUSE-ANTICHEAT-UNREVIEWED-EVIDENCE-SURFACE) that a standalone JSON request would not
usefully prove.
