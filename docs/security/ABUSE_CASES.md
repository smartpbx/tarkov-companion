# Abuse cases

STRIDE-style, one row per case. `Fixture` names the illustrative file in
`tests/security/fixtures/` where one exists. Fixtures are documentation inputs, not executed
proof; `tests/security/README.md` makes that distinction explicit. `→ Risk` is the matching row in
`CONTROLS_AND_RESIDUAL_RISK.md`.

## TB-1 / TB-2: EFT visible/file output and local storage ↔ Desktop

| ID | STRIDE | Actor | Scenario | Fixture | → Risk |
| --- | --- | --- | --- | --- | --- |
| ABUSE-ANTICHEAT-UNREVIEWED-EVIDENCE-SURFACE | Tampering / information disclosure | ACT-12 | A future recognizer adds an `enemyPosition` or equivalent field derived from a visible capture and presents it as current. The change uses no forbidden API string, so `audit-safety.sh` passes even though the data model and UI violate the no-live-enemy/no-ESP boundary. A directory or package listing would not expose the violation; review and a deterministic contract test must. | — | RISK-ANTICHEAT-REVIEW-DISCIPLINE |
| ABUSE-SCREENSHOT-RETENTION-SURPRISE | Tampering / repudiation | ACT-1 | A player has no readable screenshot-retention settings and assumes cleanup is off. The store selects its enabled 24-hour default, and a periodic retention pass moves matching old game screenshots to the recycle bin while preserving the newest. The files are recoverable and tightly selected, but the default is still a user-file mutation that must be disclosed and reviewed in #309. | — | RISK-SCREENSHOT-RETENTION-DEFAULT |

## TB-4 / TB-6: Desktop ↔ Relay ↔ Squadmate

| ID | STRIDE | Actor | Scenario | Fixture | → Risk |
| --- | --- | --- | --- | --- | --- |
| ABUSE-RELAY-NAME-COLLISION | Spoofing | ACT-4 | A member publishes `POST /state` with the same display name as an existing member. `GroupRooms` stores entries by name, so the later request silently replaces the legitimate member's state. Other members see the impostor's position under the real member's name until another publish. | `relay-state-name-collision.json` | RISK-RELAY-IDENTITY |
| ABUSE-RELAY-STALE-FRESHNESS | Tampering | ACT-4 | A client sends `"positionAge": 0.1` on every publish although its last position capture is 30 minutes old. The relay records server-observed `sinceSeconds`, but never cross-checks client-reported `positionAge`; squadmates read stale coordinates as freshly captured. | `relay-state-fabricated-freshness.json` | RISK-RELAY-CLIENT-TRUST |
| ABUSE-RELAY-WEAK-KEY-GUESS | Spoofing / information disclosure | ACT-5 | An attacker tries common strings of at least eight characters as `X-Group-Key`. On an open relay, a miss returns an empty room while a live-room hit returns member state. On a closed relay, an unregistered hash returns 403 while a guessed registered key reaches the room. No per-IP/key attempt limit or backoff exists, so each guess costs one request. | `relay-state-weak-key-brute-force.json` | RISK-RELAY-KEY-BRUTEFORCE |
| ABUSE-RELAY-WAYPOINT-FLOOD | Denial of service | ACT-4 | Starting with five legitimate waypoints, a member sends 61 new waypoints. Oldest-first eviction removes the five legitimate entries and the first spam entry, leaving 60 spam entries. The per-room cap bounds this room but lets a member erase the group's plan. | `relay-waypoint-flood.json` | RISK-RELAY-MARK-CAP |
| ABUSE-RELAY-CROSS-ROOM-MARK-GROWTH | Denial of service | ACT-5 | On an open relay, an attacker repeatedly chooses a new acceptable key and posts one waypoint. Each key creates another permanent `GroupMarks` room; no relay-wide room/waypoint cap or live expiry removes it, and every waypoint write serializes all non-empty rooms to `marks.json`. Per-room caps do not bound aggregate memory, CPU, or disk growth. | — | RISK-RELAY-MARK-CAP |
| ABUSE-RELAY-LEAVE-WRONG-NAME | Tampering | ACT-1 | A client renames mid-session and sends `DELETE /state/{current-name}` instead of the name actually published. The stale entry remains until expiry or the wrong entry is removed. Current `GroupSessionService` avoids this by retaining the registered identity, but this remains a state-transition regression case. | — | RISK-RELAY-IDENTITY |
| ABUSE-RELAY-PLAINTEXT-KEY | Information disclosure / spoofing | ACT-11 | A malicious or compromised relay process/TLS endpoint records `X-Group-Key` before calling `GroupKey.RoomFor`. It can later replay that reusable credential against the room. HTTPS protects the hop from an on-path observer; it cannot hide plaintext from the receiving process. | — | RISK-RELAY-KEY-DISCLOSURE |
| ABUSE-RELAY-CLEARTEXT-CREDENTIAL | Information disclosure / spoofing / tampering | ACT-5 | A desktop accepts a private-address `http://` relay, or a tablet/operator opens the relay's direct HTTP origin. An on-path LAN actor reads or modifies `X-Group-Key`, `X-Admin-Key`, and associated room/admin traffic, then replays the recovered credential. The optional HTTPS tunnel does not protect a direct HTTP route. | — | RISK-RELAY-KEY-DISCLOSURE |
| ABUSE-RELAY-OBSERVED-DATA-POLICY | Information disclosure / policy violation | ACT-12 | Group sharing is enabled while the own-loadout switch is off. `GroupSessionService` still reads observed party kits from game logs and serializes names, loadouts, levels, sides, and scav-lock times into `Observed`. The relay prunes names outside the current room, but third-party log-derived data has already been transmitted contrary to `docs/SAFETY.md`'s current rule. | — | RISK-RELAY-OBSERVED-DATA-POLICY |

## TB-4 / TB-5: Anonymous client ↔ Relay ↔ Upstream

| ID | STRIDE | Actor | Scenario | Fixture | → Risk |
| --- | --- | --- | --- | --- | --- |
| ABUSE-RELAY-PUBLIC-ENDPOINT-DOS | Denial of service | ACT-5 | An anonymous client sustains requests to `/catalog`, `/landmarks`, or `/search`; none requires a key or has an application request-rate limit. Cold/expired allowed catalog paths can also cause bounded upstream fetches, while repeated search and response work continues to consume relay resources. | `relay-public-endpoint-flood.json` | RISK-RELAY-NO-RATE-LIMIT |

## TB-5 / TB-10: Upstream and release channels

| ID | STRIDE | Actor | Scenario | Fixture | → Risk |
| --- | --- | --- | --- | --- | --- |
| ABUSE-CATALOG-UPSTREAM-TAMPER | Tampering | ACT-7 | `json.tarkov.dev`, or a path presenting a valid certificate, serves a manipulated catalog. `CatalogMirror` computes a SHA-256 ETag from the received bytes, which proves mirror/client consistency rather than authenticity. Every consumer accepts the changed value with no independent signature or second source. | `catalog-mirror-tamper-scenario.json` | RISK-CATALOG-INTEGRITY |
| ABUSE-CATALOG-STALE-FALLBACK | Tampering / repudiation | ACT-7 | A held snapshot is older than the one-hour freshness window and refresh fails. `CatalogMirror.GetAsync` returns the held snapshot silently; the response contains its content ETag but no failure/freshness signal. A client can make a price, quest, or map decision from old data believing the mirror request succeeded normally. | — | RISK-CATALOG-INTEGRITY |
| ABUSE-UPDATE-CHANNEL-DOWNGRADE | Elevation of privilege | ACT-8 | A compromised release channel presents an older relay artifact with its matching same-channel checksum, or manipulates the Velopack feed/package set seen by the desktop. Relay source has no independently stored minimum version; desktop project source delegates the decision to Velopack and shows no independently anchored version rule. This is a threat scenario for the full #317 cryptographic/update review, not a claim that this pass executed a downgrade. | — | RISK-UPDATE-CHANNEL-TRUST |

## TB-4: Desktop problem-report body → Relay

| ID | STRIDE | Actor | Scenario | Fixture | → Risk |
| --- | --- | --- | --- | --- | --- |
| ABUSE-REPORT-INCOMPLETE-REDACTION | Information disclosure / policy violation | ACT-12 | A player uses the ordinary Report Problem flow. `SupportBundle.Describe` inserts `Observation.Detail` verbatim, where current observation can include raw log/screenshot roots; its 120-line app-log tail can also carry raw roots, screenshot filenames, and X/Y/Z. `Redact` runs only on those tail lines and does not provide a whole-bundle allowlist. The relay accepts any non-empty body and persists it verbatim, so normal use can transmit prohibited diagnostic path segments and coordinates contrary to `docs/SAFETY.md`. | — | RISK-REPORT-REDACTION |

## TB-8 / TB-9: Operator access and report metadata automation

| ID | STRIDE | Actor | Scenario | Fixture | → Risk |
| --- | --- | --- | --- | --- | --- |
| ABUSE-ADMIN-REPORT-REFERENCE-TRAVERSAL | Tampering / information disclosure | ACT-6 if validation regresses | A caller supplies `GET /reports/../../../../etc/passwd`. `ProblemReports.Read` currently requires exactly 12 lowercase hexadecimal characters before building its filename glob, excluding `.` and `/`; this is a closed regression case, not a current exploit. | `admin-report-reference-traversal.json` | RISK-REPORT-TRAVERSAL |
| ABUSE-ADMIN-KEY-TIMING | Information disclosure | ACT-5 | A remote attacker times `X-Admin-Key` comparisons. `RelayAdmin.IsAuthorised` currently uses `CryptographicOperations.FixedTimeEquals`, preventing byte-by-byte early exit for equal-length values; different lengths may still be distinguishable. This is a closed byte-recovery regression case with the length caveat retained in the register. | — | RISK-ADMIN-KEY-TIMING |
| ABUSE-ADMIN-REPORT-FLOOD | Denial of service | ACT-5 | On an open relay, an anonymous caller posts three reports using one acceptable invented group key, then changes the key and repeats. Each key hashes to a fresh per-room/hour bucket. Kestrel limits each body to 32 KiB, but the number of buckets and retained `reports/*.md` files has no global count, TTL, or disk quota. | — | RISK-REPORT-RATE-LIMIT |

## TB-2 / TB-7: Local credential storage

| ID | STRIDE | Actor | Scenario | Fixture | → Risk |
| --- | --- | --- | --- | --- | --- |
| ABUSE-GROUP-KEY-LOCAL-RECOVERY | Information disclosure / spoofing | ACT-9 or ACT-10 | A later tablet/browser user reads `localStorage["key"]`, or same-user malware reads desktop `Config/group.json`. Both recover the reusable plaintext group key and gain the room's full read/write authority until every member rotates it. There is no per-device revocation. | — | RISK-GROUP-KEY-LOCAL-EXPOSURE |
| ABUSE-DPAPI-SAME-USER-MALWARE | Information disclosure | ACT-10 | Malware running as the same Windows user calls `CryptUnprotectData` with the application's fixed entropy against the protected file and recovers the TarkovTracker token. DPAPI `CurrentUser` defends against another OS user/offline disk copy, not code already running as the player. | — | RISK-DPAPI-SAMEUSER |

## TB-3: Desktop ↔ public data sources

| ID | STRIDE | Actor | Scenario | Fixture | → Risk |
| --- | --- | --- | --- | --- | --- |
| ABUSE-TARKOVTRACKER-REDIRECT | Elevation of privilege | ACT-7 | `api.tarkovtracker.org` returns a redirect to an attacker host while the client carries a bearer token. `TarkovTrackerApiClient` sets `AllowAutoRedirect = false`, so the redirect is returned rather than followed; this is a closed regression case. | — | RISK-TARKOVTRACKER-REDIRECT |

## TB-11: User-controlled import/export files ↔ Desktop

| ID | STRIDE | Actor | Scenario | Fixture | → Risk |
| --- | --- | --- | --- | --- | --- |
| ABUSE-LOCAL-IMPORT-REPLAY | Tampering | ACT-1 | A player imports an older but schema-valid profile envelope. `JsonFilePlayerProfileService` validates shape and bounds, then replaces current profile state without comparing the envelope's exported/updated time to the current profile. Quest exchange adds a checksum and preview, but an author of a tampered document can recompute its same-document checksum. Existing controls reduce accidents; they do not authenticate authorship or establish monotonic freshness. | — | RISK-LOCAL-IMPORT-EXPORT-INTEGRITY |
| ABUSE-LOCAL-EXPORT-DISCLOSURE | Information disclosure | ACT-1 | A player shares a raid-history export believing it is a coarse summary. Current JSON/CSV output includes profile id, map, mode, exact start/end UTC, outcome, and free-form notes for every listed raid. Export is user initiated, but no phase-1 product disclosure or minimization review establishes that the recipient scope was understood. | — | RISK-LOCAL-IMPORT-EXPORT-INTEGRITY |
