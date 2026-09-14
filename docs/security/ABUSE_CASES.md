# Abuse cases

STRIDE-style, one row per case. `Fixture` names the illustrative file in
`tests/security/fixtures/` where one exists (see that directory's `README.md` for what a fixture
is and is not). `→ Risk` is the row ID in `CONTROLS_AND_RESIDUAL_RISK.md`.

## TB-4 / TB-6: Desktop ↔ Relay ↔ Squadmate

| ID | STRIDE | Actor | Scenario | Fixture | → Risk |
| --- | --- | --- | --- | --- | --- |
| ABUSE-RELAY-NAME-COLLISION | Spoofing | ACT-4 | A member publishes `POST /state` with `"name": "MaxGooner"`, the same display name an existing member is using. The relay's storage is keyed by name, so this silently overwrites the legitimate member's entry (`GroupRooms`/`GroupContracts.cs` semantics; documented as intended behavior in `docs/GROUP_RELAY.md`, "publishing again under the same name replaces your previous entry"). Every other member now sees the impostor's position under the real member's name until the real member republishes. | `relay-state-name-collision.json` | RISK-RELAY-IDENTITY |
| ABUSE-RELAY-STALE-FRESHNESS | Tampering | ACT-4 | A member sends `"positionAge": 0.1` on every publish regardless of when the position was actually captured, since `positionAge` is entirely client-computed and never cross-checked by the relay (`docs/GROUP_RELAY.md`). Squadmates read a stale marker as fresh. | `relay-state-fabricated-freshness.json` | RISK-RELAY-CLIENT-TRUST |
| ABUSE-RELAY-WEAK-KEY-GUESS | Spoofing | ACT-5 | An attacker who knows nothing about a group tries a dictionary of common 8-16 character strings as `X-Group-Key` against an open (unregistered) relay. No rate limiting or lockout exists on `/state`, so guesses cost one HTTP request each (`GroupKey.MinimumLength = 8`, no attempt counter in `GroupKey.cs`/`Program.cs`). A successful guess joins the room silently — the relay cannot distinguish a guess from a legitimate member (`docs/GROUP_RELAY.md`: "A 401 does not mean a wrong key... a key nobody else uses names a group nobody else is in"). | `relay-state-weak-key-brute-force.json` | RISK-RELAY-KEY-BRUTEFORCE |
| ABUSE-RELAY-WAYPOINT-FLOOD | Denial of service | ACT-4 or ACT-5 (any holder of a valid key for the target room) | A member calls `POST /waypoints` 61 times in a burst against one room. The 60-waypoint cap means the 61st push evicts the oldest waypoint rather than being refused (`docs/GROUP_RELAY.md`, "Limits"). A malicious or buggy client can evict a squad's real plan by flooding. | `relay-waypoint-flood.json` | RISK-RELAY-MARK-CAP |
| ABUSE-RELAY-PUBLIC-ENDPOINT-DOS | Denial of service | ACT-5 | An anonymous client sends a sustained burst of requests to `/catalog`, `/landmarks`, or `/search` — none require a key (`RelayAccess.IsGroupPath` excludes them) and none carry a request-rate limit anywhere in `Program.cs`. `/catalog/{mode}/{endpoint}` in particular proxies to upstream on a cache miss, so a flood could also pressure `json.tarkov.dev`. | `relay-public-endpoint-flood.json` | RISK-RELAY-NO-RATE-LIMIT |
| ABUSE-RELAY-LEAVE-WRONG-NAME | Tampering (self-inflicted, but a real support cost) | ACT-1 | The client calls `DELETE /state/{name}` using the *currently configured* display name after the player renamed mid-session, rather than the name that was actually published. This removes nothing (or removes the wrong entry) and leaves the stale marker standing — the exact failure `docs/GROUP_RELAY.md` calls out by name ("Withdraw the name that was published, not the one currently configured"). | — | RISK-RELAY-IDENTITY |

## TB-7: Player ↔ Tablet

| ID | STRIDE | Actor | Scenario | Fixture | → Risk |
| --- | --- | --- | --- | --- | --- |
| ABUSE-TABLET-KEY-LEAK | Information disclosure | ACT-9 | The group key is typed into a tablet's browser (`docs/GROUP_RELAY.md`, `Tablet.cs`). The tablet has no separate identity or scope from a full desktop member — anyone who later reads that browser's history, or the device itself if lost, recovers a credential with full read/write of the room for as long as the group keeps using that key. There is no per-device revocation; the only remedy is rotating the group's key, which affects every member. | — | RISK-TABLET-NO-SCOPING |

## TB-5: Relay ↔ upstream / self-update

| ID | STRIDE | Actor | Scenario | Fixture | → Risk |
| --- | --- | --- | --- | --- | --- |
| ABUSE-CATALOG-UPSTREAM-TAMPER | Tampering | ACT-7 | `json.tarkov.dev` (or a MITM presenting a valid cert on a compromised path) serves a manipulated catalog payload. `CatalogMirror` forwards it byte-for-byte and computes its own SHA-256 as a cache tag — that hash authenticates *mirror-to-client consistency*, not *upstream authenticity*, because it is computed from whatever upstream sent rather than checked against any pinned or independently-obtained value (`CatalogMirror.cs`). Every client of that relay receives the manipulated data with the mirror's confirmation attached. | `catalog-mirror-tamper-scenario.json` | RISK-CATALOG-INTEGRITY |
| ABUSE-RELAY-UPDATE-DOWNGRADE | Elevation of privilege | ACT-8 | An attacker who can influence what `GROUPSERVER-SHA256SUMS.txt` resolves to for a target relay (DNS/TLS compromise of the release host, or compromise of the GitHub account/Action publishing it) could point `RelayUpdate` at an older, vulnerable checksum. `RelayUpdate` verifies the *checksum* of what it fetches, but the update trigger has no independent version-monotonicity check on the relay side — the anti-downgrade guarantee documented in `docs/OPERATIONS.md` ("refuses to publish a version below what is already live") is enforced entirely by the *publish* pipeline, not by the *update client*. | — | RISK-UPDATE-CHANNEL-TRUST |

## TB-8: Operator ↔ Relay

| ID | STRIDE | Actor | Scenario | Fixture | → Risk |
| --- | --- | --- | --- | --- | --- |
| ABUSE-ADMIN-REPORT-REFERENCE-TRAVERSAL | Tampering / information disclosure | ACT-6 (if the validation below were absent) | A caller supplies `GET /reports/../../../../etc/passwd` (or any non-hex, wrong-length value) as `{reference}`, attempting to read outside the reports directory. `ProblemReports.Read` validates the reference is exactly 12 lowercase-hex characters before using it in a filename glob, which structurally excludes `.` and `/` (`ProblemReports.cs`). This case is included because it is the kind of input #317 asks to be reviewed explicitly ("archive traversal in reports"), and because the control is a validation the caller could get wrong on a future change — it is not free of that risk just because it is correct today. | `admin-report-reference-traversal.json` | RISK-REPORT-TRAVERSAL (closed — see register) |
| ABUSE-ADMIN-KEY-TIMING | Information disclosure | ACT-5 | An attacker times `X-Admin-Key` comparisons to recover the admin key byte-by-byte. `RelayAdmin.IsAuthorised` uses `CryptographicOperations.FixedTimeEquals`, which is constant-time for equal-length inputs (`RelayAdmin.cs`). Included for the same reason as the traversal case — a correct control worth naming, not a live gap. | — | RISK-ADMIN-KEY-TIMING (closed — see register) |
| ABUSE-ADMIN-REPORT-FLOOD | Denial of service | ACT-5 (or a misbehaving legitimate client) | A client posts `/report` in a tight loop to exhaust the relay's report queue or generate noise for the operator. `ProblemReports.IsRateLimited` caps it at 3 per room per hour and `MaximumBytes = 64 * 1024` caps each report's size (`ProblemReports.cs`). Included as a verified-working bound, cross-referenced against ABUSE-RELAY-PUBLIC-ENDPOINT-DOS to show the contrast: `/report` has a documented limiter and `/catalog`/`/search`/`/landmarks` do not. | — | RISK-REPORT-RATE-LIMIT (closed — see register) |

## TB-2: Local filesystem ↔ Desktop

| ID | STRIDE | Actor | Scenario | Fixture | → Risk |
| --- | --- | --- | --- | --- | --- |
| ABUSE-DPAPI-SAME-USER-MALWARE | Information disclosure | ACT-10 | Malware running as the same Windows user as the player calls `CryptUnprotectData` with the same entropy value (`WindowsDpapiSecretStore.cs`, `Entropy`, a fixed non-secret constant) against the same protected file and recovers the TarkovTracker bearer token in plaintext, exactly as the legitimate application would. DPAPI `CurrentUser` scope defends against a different OS user or an offline disk copy, not against code already running as the player. | — | RISK-DPAPI-SAMEUSER |

## TB-3: Desktop ↔ public data sources

| ID | STRIDE | Actor | Scenario | Fixture | → Risk |
| --- | --- | --- | --- | --- | --- |
| ABUSE-TARKOVTRACKER-REDIRECT | Elevation of privilege | ACT-7 | `api.tarkovtracker.org` (or a MITM) responds to `GET /token` or `GET /progress` with a 3xx redirect to an attacker-controlled host, attempting to have the client leak the bearer token to it on the follow-up request. `TarkovTrackerApiClient` disables `AllowAutoRedirect` on the underlying handler (`TarkovTrackerApiClient.cs`), so the redirect is surfaced as a response rather than followed. Included as a verified-working control matching #317's explicit "session hijacking" / secret-exfiltration abuse-case request. | — | RISK-TARKOVTRACKER-REDIRECT (closed — see register) |

## Boundary this section deliberately does not model

Abuse cases against EFT itself (TB-1) — e.g., "what if the game's log format changes to include
more player data" — belong in `docs/RECOGNITION.md` / `docs/research/EFT_LOG_FACTS.md` as data
contract questions, not here: TB-1 is a one-way, read-only boundary this project never attacks or
defends against, only reads honestly. `ANTI_CHEAT_REVIEW.md` covers whether the *project's own
code* respects that boundary, which is the actual security question TB-1 raises.
