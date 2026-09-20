# Phase status

This is the integrated wave-2 status for issue
[#317](https://github.com/smartpbx/tarkov-companion/issues/317). **Issue #317 remains open.**
The six baseline audits are source-review evidence, not a penetration test. Issue #278 subsequently
added exact-head automated evidence for its paired-relay authorization core; that core remains
uncomposed and is not evidence for the request boundary or deployed behavior. High findings remain
release blockers until their named owner issues implement the complete control and exact-head
GitHub Actions (plus any stated manual `dev` verification) supplies the required evidence.

The counts below were re-derived on 2026-09-19 against `main`, not carried forward. See
[Triage of 2026-09-19](#triage-of-2026-09-19-317).

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

The integrated register contains **47 open findings and four closed findings**. Open severity is
**zero Critical, 10 High, 30 Medium, and seven Low**. Every open row has an explicit
Accept/Mitigate/Defer disposition, a named GitHub-issue owner, Reviewed source evidence, and exact
verification required before its status can change.

### Triage of 2026-09-19 (#317)

The fifteen open High findings were re-read against `main`, not against the audit's own snapshot.
Five moved, and each moved for a stated reason:

| Finding | Was | Now | Why |
| --- | --- | --- | --- |
| RISK-EXTERNAL-DATA-BOUNDS | High | Medium | The desktop reader had already become bounded (`ReadBoundedUtf8Async` against a 32 MB ceiling, checked on the declared length and again while streaming). The relay's two outbound clients had a timeout and no ceiling; they now carry `MaxResponseContentBufferSize`. No reader accepts an unbounded body. What follows acceptance — decompressed size, element counts, string lengths — is still only bounded by the byte ceiling. |
| RISK-PERSISTENCE-SCHEMA-COMPATIBILITY | High | Medium | The fence the finding asked for exists: `SqliteMigrationRunner` refuses to apply a known missing migration to a database carrying an unknown newer one, leaving the original intact. Resource checksums remain absent. |
| RISK-RELAY-KEY-BRUTEFORCE | High | Medium | Guessing now costs something. `RelayAttemptLimiter` delays from the fifth refused key and refuses for a minute after twenty in five minutes. Key entropy is unchanged. |
| RISK-ADMIN-KEY-BRUTEFORCE | High | Medium | The same limiter covers `/admin*` and `/reports*`. No entropy floor was added: refusing a short configured key at startup would take the deployed relay down on upgrade, which is the operator's decision to make. |
| RISK-RELAY-REGISTRY-FAIL-OPEN | High | **Closed** | An unreadable `rooms.json` refuses every room and says so with a 503, instead of clearing the list — which meant open. Fixed and tested rather than re-reviewed. |

The remaining ten Highs were re-read against source on 2026-09-19 and every one is still open.
What that reading found:

- **Three share one cause.** RISK-RELAY-IDENTITY, RISK-RELAY-KEY-DISCLOSURE and
  RISK-PAIRED-AUTH-COMPOSITION all wait on the same thing: `RelayHttpSecurity` and the paired
  authorization core merged under #278 are **still not composed**. Searching the repository finds
  `RelayHttpSecurity` referenced only by its own test file. (The same is true of the paired half of
  the registry finding closed above, whose `VerifiedRelayRegistryStore` is likewise uncomposed.)
  Composing them is the single change that would move the most of this register, and it is
  #278/#294 work rather than an audit repair.
- **One is a contradiction, not a defect, and needs a decision rather than a patch.**
  RISK-RELAY-OBSERVED-DATA-POLICY: the relay prunes `Observed` entries to the names currently in
  the room, which bounds who receives them, while `docs/SAFETY.md` says log-derived data about
  another player is *never transmitted*. Both cannot stand. Either the rule is stricter than it was
  meant to be — the same document lists a player's own party's composition as permitted, and these
  are the people it is being sent to — or the relay must stop sending the field. #294's acceptance
  asks for exactly this kind of contradiction to be reconciled against verified behaviour; the
  reconciliation is Clayton's call and is recorded here rather than silently resolved either way.
- **Six were confirmed by reading the named code and remain as written.** RISK-UPDATE-CHANNEL-TRUST
  (#280's rings still do not exist — `capture_controls.py` records all three release environments
  as absent), RISK-REPORT-REDACTION (`/report` caps a body at 64 KiB and still accepts free text
  that can name the reporter), RISK-PROFILE-IMPORT-STATE (import validates and replaces, with no
  freshness or scope comparison), RISK-CONTEXT-ISOLATION (`SqliteRaidHistoryService`'s list query
  selects `profile_id` and `mode` without filtering on them, and the trail query filters only on
  map), RISK-WATCHED-PATH-CONTAINMENT (no canonical-root or final-handle check exists on the
  watched paths) and RISK-NATIVE-OCR-SUPPLY-CHAIN (no `NativeLibrary`, `DllImportResolver`,
  `SetDllDirectory` or Authenticode policy appears anywhere in the source).

The narrow issue-#278 evidence cited by the register is reproducible: exact head
`07c53aeedb094a21a1bea585b8fab3122ba92dbc` passed
[CI](https://github.com/smartpbx/tarkov-companion/actions/runs/35068464421),
[Windows verification](https://github.com/smartpbx/tarkov-companion/actions/runs/35068464515), and
[License lock](https://github.com/smartpbx/tarkov-companion/actions/runs/35068464435). Those runs
cover the core source, not its future #294 request routing or deployment.

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
| Paired live-session bearer theft | ABUSE-PAIRED-LIVE-BEARER-THEFT | RISK-GROUP-KEY-LOCAL-EXPOSURE |
| Paired handshake/source/recovery trust handoff | ABUSE-PAIRED-HANDSHAKE-AUTHORITY-INJECTION, ABUSE-PAIRED-RATE-PARTITION-SPOOF, ABUSE-PAIRED-OWNER-RECOVERY-EXPOSURE | RISK-PAIRED-AUTH-COMPOSITION |
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
| Issue #278 paired authorization core / composition follow-up | ABUSE-PAIRED-HANDSHAKE-AUTHORITY-INJECTION, ABUSE-PAIRED-RATE-PARTITION-SPOOF, ABUSE-PAIRED-OWNER-RECOVERY-EXPOSURE, ABUSE-PAIRED-LIVE-BEARER-THEFT | RISK-PAIRED-AUTH-COMPOSITION, RISK-GROUP-KEY-LOCAL-EXPOSURE |
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
   authorization and paired-auth composition, registry fail-open, report privacy, watched-path
   containment, context isolation, native OCR loading, and release/update trust.
3. Add executable hostile-input/state/race/platform tests named by the register. Existing baseline
   audit test names were read but not run. The issue-#278 entries explicitly marked
   **Tested (automated)** are the narrow exception, backed by exact-head CI run `35068464421`; they
   do not prove future composition or staging behavior.
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
