# Phase status

This is the integrated wave-2 status for issue
[#317](https://github.com/smartpbx/tarkov-companion/issues/317). **Issue #317 remains open.**
The six audits are source-review evidence, not a penetration test, an observed automated run, or
proof that any recommended mitigation exists. High findings remain release blockers until their
named owner issues implement the control and exact-head GitHub Actions (plus any stated manual
`dev` verification) supplies the required evidence.

## Wave-2 audit coverage

| Lane | Integrated document | Standalone draft evidence superseded by this integration | Finding scope |
| --- | --- | --- | --- |
| Serialization and external data | [audits/SERIALIZATION_AND_EXTERNAL_DATA.md](audits/SERIALIZATION_AND_EXTERNAL_DATA.md) | #323 | SER-*; current hostile JSON/HTTP/file readers plus V2 transport prerequisite |
| Cryptography, secrets, transport, and updates | [audits/CRYPTO_SECRETS_AND_UPDATES.md](audits/CRYPTO_SECRETS_AND_UPDATES.md) | #322 | Key entropy/lifecycle, DPAPI, relay/admin credentials, release/update chain |
| Windows platform boundaries | [audits/WINDOWS_PLATFORM_BOUNDARIES.md](audits/WINDOWS_PLATFORM_BOUNDARIES.md) | #324 | WIN-001 through WIN-014; P/Invoke, capture, watchers, OCR, install/update |
| Diagnostics, errors, and privacy | [audits/DIAGNOSTICS_ERRORS_AND_PRIVACY.md](audits/DIAGNOSTICS_ERRORS_AND_PRIVACY.md) | #325, standalone head `ddf621d` | DIAG-01 through DIAG-10; report, error, retention, buffer, developer-channel and outbound truth |
| Persistence and local state | [audits/PERSISTENCE_AND_LOCAL_STATE.md](audits/PERSISTENCE_AND_LOCAL_STATE.md) | #326 | PERS-01 through PERS-12; migrations, recovery, scope, outbox/export and file races |
| Relay authorization and state | [audits/RELAY_AUTHORIZATION_AND_STATE.md](audits/RELAY_AUTHORIZATION_AND_STATE.md) | #327 | RELAY-AUTH/STATE/BROWSER/OPS/TRUST; endpoint authority, ordering, persistence and browser policy |

This integration will supersede the six standalone draft PRs #322–#327. Their findings are not
discarded: each maps below to one canonical abuse case and risk entry, with duplicate scenarios
sharing the same stable risk rather than inflating counts.

## Canonical register status

The integrated register contains **47 open findings and three closed findings**. Open severity is
**zero Critical, 14 High, 26 Medium, and seven Low**. Every open row has an explicit
Accept/Mitigate/Defer disposition, a named GitHub-issue owner, Reviewed source evidence, and exact
verification required before its status can change.

These counts are reproducible from the Markdown tables, not manually asserted:

```sh
sed -n '/^## Open findings/,/^## Closed findings/p' docs/security/CONTROLS_AND_RESIDUAL_RISK.md \
  | rg '^\| RISK-.*\*\*(Critical|High|Medium|Low)\*\*' | wc -l
sed -n '/^## Open findings/,/^## Closed findings/p' docs/security/CONTROLS_AND_RESIDUAL_RISK.md \
  | rg '^\| RISK-' | rg -o '\*\*(Critical|High|Medium|Low)\*\*' | sort | uniq -c
sed -n '/^## Closed findings/,/^## Remaining verification/p' docs/security/CONTROLS_AND_RESIDUAL_RISK.md \
  | rg '^\| RISK-' | wc -l
```

## Complete wave-2 reconciliation crosswalk

An audit ID identifies where the evidence was developed. The abuse and risk IDs below are the
canonical identities used for release status. Positive-control rows remain mapped so they cannot
be misread as closing a broader risk.

### Serialization and external data

| Audit evidence | Canonical abuse case | Canonical risk |
| --- | --- | --- |
| SER-HTTP-UNBOUNDED-CATALOG | ABUSE-DESKTOP-CATALOG-RESOURCE-EXHAUSTION | RISK-EXTERNAL-DATA-BOUNDS |
| SER-RELAY-CATALOG-UNBOUNDED | ABUSE-RELAY-CATALOG-RESOURCE-EXHAUSTION | RISK-EXTERNAL-DATA-BOUNDS |
| SER-RELAY-LANDMARKS-UNBOUNDED | ABUSE-RELAY-LANDMARK-RESOURCE-EXHAUSTION | RISK-EXTERNAL-DATA-BOUNDS |
| SER-PROFILE-SCHEMA-AMPLIFICATION | ABUSE-PROFILE-SCHEMA-AMPLIFICATION | RISK-LOCAL-IMPORT-EXPORT-INTEGRITY |
| SER-CSV-FORMULA-INJECTION | ABUSE-CSV-FORMULA-INJECTION | RISK-LOCAL-IMPORT-EXPORT-INTEGRITY |
| SER-MIGRATION-NEWER-SCHEMA | ABUSE-PERSISTENCE-NEWER-SCHEMA | RISK-PERSISTENCE-SCHEMA-COMPATIBILITY |
| SER-MAP-ASSET-REDIRECT-ORIGIN | ABUSE-MAP-ASSET-REDIRECT | RISK-MAP-ASSET-ORIGIN |
| SER-MAP-CATALOG-TRUST | ABUSE-MAP-CATALOG-CONFIGURATION | RISK-MAP-CATALOG-CONFIGURATION |
| SER-QUEST-IMPORT-BOUNDS (positive control) | ABUSE-LOCAL-IMPORT-REPLAY | RISK-LOCAL-IMPORT-EXPORT-INTEGRITY |
| SER-TARKOVTRACKER-BOUNDED-READ (positive control) | ABUSE-TARKOVTRACKER-REDIRECT | RISK-TARKOVTRACKER-REDIRECT (closed narrow regression) |
| SER-V2-CONTRACT-TRANSPORT-PRECONDITION | ABUSE-DESKTOP-CATALOG-RESOURCE-EXHAUSTION | RISK-EXTERNAL-DATA-BOUNDS |

### Cryptography, secrets, transport, and updates

| Audit evidence | Canonical abuse case | Canonical risk |
| --- | --- | --- |
| Generated-key `% 31` encoding | ABUSE-GROUP-KEY-GENERATION-BIAS | RISK-GROUP-KEY-GENERATION-BIAS |
| Weak user key / online room oracle | ABUSE-RELAY-WEAK-KEY-GUESS | RISK-RELAY-KEY-BRUTEFORCE |
| Weak admin key / unlimited guesses | ABUSE-ADMIN-KEY-GUESS | RISK-ADMIN-KEY-BRUTEFORCE |
| Plaintext/shared key transport and endpoint visibility | ABUSE-RELAY-PLAINTEXT-KEY, ABUSE-RELAY-CLEARTEXT-CREDENTIAL | RISK-RELAY-KEY-DISCLOSURE |
| Reusable credential/member replay | ABUSE-RELAY-NAME-COLLISION | RISK-RELAY-IDENTITY |
| Desktop/browser group-key storage | ABUSE-GROUP-KEY-LOCAL-RECOVERY | RISK-GROUP-KEY-LOCAL-EXPOSURE |
| DPAPI same-user boundary | ABUSE-DPAPI-SAME-USER-MALWARE | RISK-DPAPI-SAMEUSER |
| Diagnostic token command-file lifetime | ABUSE-DIAGNOSTIC-CHANNEL-RETENTION | RISK-DIAGNOSTIC-CHANNEL-RETENTION |
| Release authenticity, downgrade, build tools/actions and feed publication | ABUSE-UPDATE-CHANNEL-DOWNGRADE | RISK-UPDATE-CHANNEL-TRUST |
| Relay installed/refused stamp ordering | ABUSE-RELAY-UPDATE-STALE-STAMP | RISK-RELAY-UPDATE-STATE |
| Fixed-time admin comparison | ABUSE-ADMIN-KEY-TIMING | RISK-ADMIN-KEY-TIMING (closed narrow regression) |
| TarkovTracker redirect refusal | ABUSE-TARKOVTRACKER-REDIRECT | RISK-TARKOVTRACKER-REDIRECT (closed narrow regression) |

### Diagnostics, errors, and privacy

| Audit evidence | Canonical abuse case | Canonical risk |
| --- | --- | --- |
| DIAG-01, DIAG-02, DIAG-06 | ABUSE-REPORT-INCOMPLETE-REDACTION | RISK-REPORT-REDACTION |
| DIAG-03 | ABUSE-ERROR-DETAIL-DISCLOSURE | RISK-ERROR-DISCLOSURE |
| DIAG-04 | ABUSE-DEGRADED-STATE-HIDDEN | RISK-DEGRADED-STATE-INTEGRITY |
| DIAG-05 | ABUSE-ADMIN-REPORT-FLOOD | RISK-REPORT-RATE-LIMIT |
| DIAG-07 | ABUSE-SCREENSHOT-RETENTION-SURPRISE | RISK-SCREENSHOT-RETENTION-DEFAULT |
| DIAG-08 | ABUSE-CAPTURE-BUFFER-RESIDUE | RISK-CAPTURE-BUFFER-LIFETIME |
| DIAG-09 | ABUSE-DIAGNOSTIC-CHANNEL-RETENTION | RISK-DIAGNOSTIC-CHANNEL-RETENTION |
| DIAG-10 | ABUSE-OUTBOUND-SURFACE-DRIFT | RISK-OUTBOUND-INVENTORY |

### Persistence and local state

| Audit evidence | Canonical abuse case | Canonical risk |
| --- | --- | --- |
| PERS-01 | ABUSE-PERSISTENCE-NEWER-SCHEMA | RISK-PERSISTENCE-SCHEMA-COMPATIBILITY |
| PERS-02, PERS-11 | ABUSE-PERSISTENCE-RECOVERY-FAILURE | RISK-PERSISTENCE-RECOVERY |
| PERS-03 | ABUSE-PROFILE-CONTEXT-REPLACEMENT | RISK-PROFILE-IMPORT-STATE |
| PERS-04 | ABUSE-SCREENSHOT-RETENTION-SURPRISE | RISK-SCREENSHOT-RETENTION-DEFAULT |
| PERS-05, PERS-10 | ABUSE-LOCAL-FILE-PATH-SUBSTITUTION | RISK-LOCAL-FILE-IDENTITY |
| PERS-06, PERS-12 | ABUSE-LOCAL-JSON-SILENT-RESET | RISK-LOCAL-STATE-RECOVERY |
| PERS-07 | ABUSE-RAID-HISTORY-DROPPED-EXPORT | RISK-RAID-HISTORY-DURABILITY |
| PERS-08 | ABUSE-CSV-FORMULA-INJECTION | RISK-LOCAL-IMPORT-EXPORT-INTEGRITY |
| PERS-09 | ABUSE-CONTEXT-HISTORY-CROSSOVER | RISK-CONTEXT-ISOLATION |

PERS-03 and PERS-09 retain their audit's High severity as distinct active-context and
cross-context-disclosure risks. They are not hidden in or used to raise the older Medium
`RISK-LOCAL-IMPORT-EXPORT-INTEGRITY` umbrella, which remains the canonical risk for bounded-file
schema/replay/export and CSV concerns such as PERS-08.

### Relay authorization and state

| Audit evidence | Canonical abuse case | Canonical risk |
| --- | --- | --- |
| RELAY-AUTH-01 | ABUSE-RELAY-NAME-COLLISION, ABUSE-RELAY-LEAVE-WRONG-NAME | RISK-RELAY-IDENTITY |
| RELAY-AUTH-02 | ABUSE-RELAY-WEAK-KEY-GUESS | RISK-RELAY-KEY-BRUTEFORCE |
| RELAY-AUTH-03 | ABUSE-RELAY-PLAINTEXT-KEY, ABUSE-RELAY-CLEARTEXT-CREDENTIAL | RISK-RELAY-KEY-DISCLOSURE |
| RELAY-AUTH-04 | ABUSE-RELAY-REGISTRY-FAIL-OPEN | RISK-RELAY-REGISTRY-FAIL-OPEN |
| RELAY-STATE-01 | ABUSE-RELAY-OUT-OF-ORDER-STATE | RISK-RELAY-ORDERING |
| RELAY-STATE-02 | ABUSE-RELAY-SILENT-CAPACITY | RISK-RELAY-CAPACITY |
| RELAY-STATE-03, RELAY-STATE-04 | ABUSE-RELAY-WAYPOINT-FLOOD, ABUSE-RELAY-CROSS-ROOM-MARK-GROWTH | RISK-RELAY-MARK-CAP |
| RELAY-BROWSER-01 | ABUSE-RELAY-BROWSER-CONTEXT | RISK-RELAY-BROWSER-HARDENING |
| RELAY-OPS-01 | ABUSE-RELAY-PUBLIC-ENDPOINT-DOS, ABUSE-ADMIN-REPORT-FLOOD | RISK-RELAY-NO-RATE-LIMIT, RISK-REPORT-RATE-LIMIT |
| RELAY-TRUST-01 | ABUSE-RELAY-STALE-FRESHNESS | RISK-RELAY-CLIENT-TRUST |

### Windows platform boundaries

| Audit evidence | Canonical abuse case | Canonical risk |
| --- | --- | --- |
| WIN-001 | ABUSE-WINDOW-CAPTURE-HANDLE-SWAP | RISK-WINDOW-CAPTURE-IDENTITY |
| WIN-002, WIN-010 | ABUSE-CAPTURE-INTEGER-OVERFLOW | RISK-CAPTURE-BOUNDS |
| WIN-003, WIN-009 | ABUSE-WINDOW-NATIVE-LIFETIME | RISK-WINDOW-NATIVE-LIFETIME |
| WIN-004 | ABUSE-WINDOW-DPI-MISMATCH | RISK-WINDOW-DPI |
| WIN-005 | ABUSE-WATCHED-PATH-REPARSE | RISK-WATCHED-PATH-CONTAINMENT |
| WIN-006 | ABUSE-WATCHER-UNBOUNDED-SEEN-SET | RISK-WATCHER-BOUNDS |
| WIN-007 | ABUSE-LOCAL-FILE-PATH-SUBSTITUTION | RISK-LOCAL-FILE-IDENTITY |
| WIN-008 | ABUSE-NATIVE-OCR-LOAD-HIJACK | RISK-NATIVE-OCR-SUPPLY-CHAIN |
| WIN-011 | ABUSE-SHELL-LAYOUT-CORRUPTION | RISK-SHELL-LAYOUT-RECOVERY |
| WIN-012 | ABUSE-UPDATE-CHANNEL-DOWNGRADE | RISK-UPDATE-CHANNEL-TRUST |
| WIN-013 | ABUSE-INSTALL-ELEVATION | RISK-INSTALL-PRIVILEGE |
| WIN-014 | ABUSE-ANTICHEAT-UNREVIEWED-EVIDENCE-SURFACE | RISK-ANTICHEAT-REVIEW-DISCIPLINE |

WIN-014 is deliberately Medium, matching the canonical current review-discipline gap. If a
future change actually reads EFT memory, generates game input, or renders an in-game overlay,
that implementation breach is Critical; the current source review found no such breach.

## What #317 still requires

1. Implement and verify every Mitigate/Defer control in its named issue; documentation does not
   make the current behavior safe.
2. Resolve every open High before v2 release, including external-body bounds, relay/admin
   authorization, registry fail-open, report privacy, watched-path containment, context isolation,
   native OCR loading, and release/update trust.
3. Add executable hostile-input/state/race/platform tests named by the register. Existing test
   names were read but not run and are not **Tested (automated)** evidence.
4. Attach exact-head passing GitHub Actions for `scripts/build.sh`/`scripts/test.sh` or their
   workflow equivalents, `scripts/audit-safety.sh`, secret/license checks, and each new security
   assertion. Add stated Windows/staging/manual `dev` evidence where the risk requires it.
5. Re-review future #269–#316 component implementations against the same stable IDs and update
   this living register rather than creating unindexed standalone findings.

## Local verification boundary

This integration runs documentation/link/identifier/diff and existing lightweight lexical static
checks only. It does not run `dotnet`, MSBuild, repository build/test scripts, a debugger,
Docker/container/VM, simulator/capture, network attack, or EFT-facing operation. Exact-head CI is
still required integration evidence.
