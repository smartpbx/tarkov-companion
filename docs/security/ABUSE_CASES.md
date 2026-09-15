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

## TB-4 / TB-9: Report ingestion → metadata automation

| ID | STRIDE | Actor | Scenario | Fixture | → Risk |
| --- | --- | --- | --- | --- | --- |
| ABUSE-ADMIN-REPORT-FLOOD | Denial of service | ACT-5 | On an open relay, an anonymous caller posts three reports using one acceptable invented group key, then changes the key and repeats. Each key hashes to a fresh per-room/hour bucket. Kestrel limits each body to 32 KiB, but the number of buckets and retained `reports/*.md` files has no global count, TTL, or disk quota. `relay-watch.yml` can then create GitHub issues containing the unbounded reports' reference, size, and received-time metadata, though no report body crosses TB-9. | — | RISK-REPORT-RATE-LIMIT |

## TB-8: Operator access

| ID | STRIDE | Actor | Scenario | Fixture | → Risk |
| --- | --- | --- | --- | --- | --- |
| ABUSE-ADMIN-REPORT-REFERENCE-TRAVERSAL | Tampering / information disclosure | ACT-6 if validation regresses | A caller supplies `GET /reports/../../../../etc/passwd`. `ProblemReports.Read` currently requires exactly 12 lowercase hexadecimal characters before building its filename glob, excluding `.` and `/`; this is a closed regression case, not a current exploit. | `admin-report-reference-traversal.json` | RISK-REPORT-TRAVERSAL |
| ABUSE-ADMIN-KEY-TIMING | Information disclosure | ACT-5 | A remote attacker times `X-Admin-Key` comparisons. `RelayAdmin.IsAuthorised` currently uses `CryptographicOperations.FixedTimeEquals`, preventing byte-by-byte early exit for equal-length values; different lengths may still be distinguishable. This is a closed byte-recovery regression case with the length caveat retained in the register. | — | RISK-ADMIN-KEY-TIMING |

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

## Wave 2: external-data and serialization boundaries

| ID | STRIDE | Actor | Scenario | Fixture | → Risk |
| --- | --- | --- | --- | --- | --- |
| ABUSE-DESKTOP-CATALOG-RESOURCE-EXHAUSTION | Denial of service | ACT-7 | A catalog origin or configured mirror returns a chunked or highly compressed response with no useful `Content-Length`. The desktop fully materializes decompressed text before validation; System.Text.Json's default depth 64 does not bound total bytes, arrays, or strings. | — | RISK-EXTERNAL-DATA-BOUNDS |
| ABUSE-RELAY-CATALOG-RESOURCE-EXHAUSTION | Denial of service | ACT-7 | `json.tarkov.dev` returns one large catalog body. `CatalogMirror` accepts it with `GetByteArrayAsync`, parses it, retains the identity bytes, and creates a gzip copy without a byte/count/string bound. | — | RISK-EXTERNAL-DATA-BOUNDS |
| ABUSE-RELAY-LANDMARK-RESOURCE-EXHAUSTION | Denial of service | ACT-7 | The map-artwork origin returns a large places document. `Landmarks` materializes the body, copies it for `JsonDocument.Parse`, and expands nested maps, variants, labels, strings, and position arrays without response or aggregate bounds. | — | RISK-EXTERNAL-DATA-BOUNDS |
| ABUSE-PROFILE-SCHEMA-AMPLIFICATION | Tampering / denial of service | ACT-1 or ACT-10 | A profile file remains below 1 MiB but uses many collection entries, long repeated names, duplicate properties, or nesting up to the serializer's default depth. Parsing consumes memory/CPU or selects a later duplicate before replacing active state. | — | RISK-LOCAL-IMPORT-EXPORT-INTEGRITY |
| ABUSE-CSV-FORMULA-INJECTION | Tampering | ACT-1 | A raid outcome or note begins with `=`, `+`, `-`, `@`, tab, or carriage return. CSV quoting preserves the cell but does not neutralize spreadsheet interpretation when the player or a recipient opens the export. | — | RISK-LOCAL-IMPORT-EXPORT-INTEGRITY |
| ABUSE-MAP-ASSET-REDIRECT | Tampering | ACT-7 | An initially approved map-asset URL redirects to an unapproved HTTPS host. The cache validates only the first URI, accepts the final bytes, and stores attribution for the original provider. | — | RISK-MAP-ASSET-ORIGIN |
| ABUSE-MAP-CATALOG-CONFIGURATION | Tampering | ACT-12 | A future configuration surface supplies an arbitrary HTTPS map-catalog URI through the existing options seam. Without an approved-host/final-redirect rule, hostile content is cached with only a self-consistency hash. | — | RISK-MAP-CATALOG-CONFIGURATION |

## Wave 2: persistence and local-state boundaries

| ID | STRIDE | Actor | Scenario | Fixture | → Risk |
| --- | --- | --- | --- | --- | --- |
| ABUSE-PERSISTENCE-NEWER-SCHEMA | Tampering / denial of service | ACT-1 | A newer build records an unknown SQLite migration, then an older build opens the database with a known migration missing from its ledger. The older runner reports the unknown version but can still apply SQL to a schema it does not understand. | — | RISK-PERSISTENCE-SCHEMA-COMPATIBILITY |
| ABUSE-PERSISTENCE-RECOVERY-FAILURE | Tampering / repudiation | ACT-1 | Backup creation fails for disk/permission reasons and migration continues, or a corrupt row is parsed as an invented default or aborts a whole read. No verified restore/quarantine state tells the player which evidence remains trustworthy. | — | RISK-PERSISTENCE-RECOVERY |
| ABUSE-PROFILE-CONTEXT-REPLACEMENT | Tampering / information disclosure | ACT-1 | A stale or wrong-scope profile import immediately replaces `profile.json` while quest and raid records remain independently scoped in SQLite. Current state can become internally inconsistent before any conflict/restore decision. | — | RISK-PROFILE-IMPORT-STATE |
| ABUSE-LOCAL-FILE-PATH-SUBSTITUTION | Tampering / information disclosure | ACT-10 | A same-user process wins a fixed-temp, parent-junction, reparse, or check-then-act race during JSON/secret/screenshot operations. The later pathname names a different target than the one validated or selected. | — | RISK-LOCAL-FILE-IDENTITY |
| ABUSE-LOCAL-JSON-SILENT-RESET | Tampering / repudiation | ACT-1 | A malformed map-preference or event file is treated as absent. A later write silently overwrites recoverable bytes or an invalid definition disappears without a rejected-evidence state. | — | RISK-LOCAL-STATE-RECOVERY |
| ABUSE-RAID-HISTORY-DROPPED-EXPORT | Repudiation / information disclosure | ACT-1 | A queued raid write fails five times and is discarded, or export races the queue before a pending write lands. The exported history looks complete but has no durable gap/pending marker. | — | RISK-RAID-HISTORY-DURABILITY |
| ABUSE-CONTEXT-HISTORY-CROSSOVER | Information disclosure | ACT-1 | After a profile/mode/generation change, history listing, trails, or exports read globally or filter only by map. Rows from another context appear under the active one without explicit comparison labeling. | — | RISK-CONTEXT-ISOLATION |

## Wave 2: diagnostics, errors, and local privacy

| ID | STRIDE | Actor | Scenario | Fixture | → Risk |
| --- | --- | --- | --- | --- | --- |
| ABUSE-ERROR-DETAIL-DISCLOSURE | Information disclosure | ACT-7 or ACT-10 | A path, token, endpoint, or attacker-chosen newline enters `exception.Message`. UI/console error paths display the raw detail, and copied diagnostics can carry it into the report path. | — | RISK-ERROR-DISCLOSURE |
| ABUSE-DEGRADED-STATE-HIDDEN | Tampering / repudiation | ACT-12 | An unhandled UI callback is marked handled after partial mutation, group withdrawal fails silently, or shutdown abandons work. The process continues without a durable sanitized degraded-state signal. | — | RISK-DEGRADED-STATE-INTEGRITY |
| ABUSE-CAPTURE-BUFFER-RESIDUE | Information disclosure | ACT-12 | A decode/cancellation/error path leaves managed full-image or cropped pixel buffers reachable until nondeterministic garbage collection, and a future debug path accidentally retains them. | — | RISK-CAPTURE-BUFFER-LIFETIME |
| ABUSE-DIAGNOSTIC-CHANNEL-RETENTION | Information disclosure / denial of service | ACT-10 | A same-user developer process writes many diagnostic commands. Token-bearing `*.command.json` files and response files have no crash-safe TTL/count/byte cleanup; interrupted processing can retain the token and grow storage. | — | RISK-DIAGNOSTIC-CHANNEL-RETENTION |
| ABUSE-OUTBOUND-SURFACE-DRIFT | Information disclosure | ACT-12 | A future collector or new network path is added without the current offline composition, explicit feature consent, inspectable inventory, revocation, or no-unsolicited-request regression gate. | — | RISK-OUTBOUND-INVENTORY |

## Wave 2: relay authorization and state machines

| ID | STRIDE | Actor | Scenario | Fixture | → Risk |
| --- | --- | --- | --- | --- | --- |
| ABUSE-ADMIN-KEY-GUESS | Spoofing / information disclosure / elevation of privilege | ACT-5 | An operator configures a short/common admin key. An actor makes unlimited guesses against admin/report routes; fixed-time equality does not add entropy or rate limiting, and a hit grants room management, update requests, and retained reports. | — | RISK-ADMIN-KEY-BRUTEFORCE |
| ABUSE-GROUP-KEY-GENERATION-BIAS | Spoofing | ACT-5 | An attacker ranks the eight slightly more probable symbols in a generated key's `% 31` encoding. The worst-case min-entropy is about 77.28 bits, still impractical to exhaust; the bias is retained as an explicit accepted residual rather than called uniform 80-bit output. | — | RISK-GROUP-KEY-GENERATION-BIAS |
| ABUSE-RELAY-REGISTRY-FAIL-OPEN | Elevation of privilege | ACT-5 | A closed relay's `rooms.json` becomes truncated or unreadable. `GroupRoomRegistry.Load` clears the registry, empty means open, and an invented acceptable group key can then create/use state. | — | RISK-RELAY-REGISTRY-FAIL-OPEN |
| ABUSE-RELAY-OUT-OF-ORDER-STATE | Tampering | ACT-4 | Two publishes or mark mutations overlap; the later state reply arrives first and the older reply is applied last. No client sequence or server room revision rejects the stale response. | — | RISK-RELAY-ORDERING |
| ABUSE-RELAY-SILENT-CAPACITY | Denial of service / repudiation | ACT-4 | Sixteen names fill a room, then a seventeenth valid publish returns ordinary 200 state without admitting the sender. A concurrent capacity check can also race because count/check/add is not one atomic admission decision. | — | RISK-RELAY-CAPACITY |
| ABUSE-RELAY-BROWSER-CONTEXT | Spoofing / information disclosure | ACT-5 | A same-origin injection or framed credential page acts with a key held in browser storage. The relay emits no CSP/frame/referrer/permissions/cache policy and does not validate Origin or Fetch Metadata. | — | RISK-RELAY-BROWSER-HARDENING |
| ABUSE-RELAY-UPDATE-STALE-STAMP | Tampering / repudiation | ACT-8 | A checksum-valid relay build fails health after the updater writes its installed stamp. Rollback restores the old tree but not the stamp; the next run exits “already on” before the refused check and admin status can name the rejected build. | — | RISK-RELAY-UPDATE-STATE |

## Wave 2: Windows platform boundaries

| ID | STRIDE | Actor | Scenario | Fixture | → Risk |
| --- | --- | --- | --- | --- | --- |
| ABUSE-WINDOW-CAPTURE-HANDLE-SWAP | Spoofing / information disclosure | ACT-10 | A process with an allowed name exits or its HWND is recycled after discovery. Capture binds the stale handle without revalidating PID/executable identity and can copy pixels from another visible window. | — | RISK-WINDOW-CAPTURE-IDENTITY |
| ABUSE-CAPTURE-INTEGER-OVERFLOW | Tampering / denial of service | ACT-12 | Extreme rectangle/image dimensions wrap unchecked region sums or byte calculations in GDI/OCR paths. A malformed in-process request passes nominal containment, copies unintended visible pixels, throws natively, or allocates excessively. | — | RISK-CAPTURE-BOUNDS |
| ABUSE-WINDOW-NATIVE-LIFETIME | Denial of service | ACT-12 | GDI `SelectObject` failure, unrestored DPI context, or concurrent OCR disposal leaves a native object selected/disposed while work is active. Repetition leaks handles or loses OCR/capture availability. | — | RISK-WINDOW-NATIVE-LIFETIME |
| ABUSE-WINDOW-DPI-MISMATCH | Tampering | ACT-1 | Mixed scaling and negative monitor origins put Win32 physical rectangles and Avalonia coordinates in different spaces, offsetting a capture or restored standalone window. | — | RISK-WINDOW-DPI |
| ABUSE-WATCHED-PATH-REPARSE | Information disclosure | ACT-10 | A configured/discovered EFT root or nested directory becomes a junction/reparse point between discovery, enumeration, and open. Recursive log watching or screenshot decode reads non-EFT local content. | — | RISK-WATCHED-PATH-CONTAINMENT |
| ABUSE-WATCHER-UNBOUNDED-SEEN-SET | Denial of service | ACT-10 | A long-lived screenshot directory receives many create/replace events. The watcher marks paths seen before successful downstream read and retains the set without a bound. | — | RISK-WATCHER-BOUNDS |
| ABUSE-NATIVE-OCR-LOAD-HIJACK | Elevation of privilege | ACT-8 or ACT-10 | A native OCR dependency resolves from a writable search location, or a compromised build/release substitutes an unsigned native binary. Model hashing does not authenticate DLL loading. | — | RISK-NATIVE-OCR-SUPPLY-CHAIN |
| ABUSE-SHELL-LAYOUT-CORRUPTION | Denial of service | ACT-1 | Hand-edited NaN, infinity, extreme, or stale mixed-DPI layout coordinates are cast/restored without full finiteness/space validation, making the standalone companion hard to reach. | — | RISK-SHELL-LAYOUT-RECOVERY |
| ABUSE-INSTALL-ELEVATION | Elevation of privilege | ACT-12 | An installer/updater is launched elevated or extracts through an unsafe temporary/reparse path. A per-user replacement flow gains broader filesystem impact even though source does not intentionally request elevation. | — | RISK-INSTALL-PRIVILEGE |
