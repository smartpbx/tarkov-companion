# Relay authorization and state-machine audit

**Scope:** #317 adversarial review lane for the current group relay implementation.
**Reviewed revision:** `d59d1361fe02290a38607c0fbd936676af290800`.
**Method:** source-grounded static review only. No requests were sent, no service was started, and no test/build/debug/container job was run. Every implementation claim below is therefore **Reviewed**, never **Tested (automated)** or **Tested (manual)**; evidence-tier meanings are in [`../METHODOLOGY.md`](../METHODOLOGY.md).

This is an external, read-only companion review. It neither reads game memory nor game traffic, injects/hooks, generates input, creates an in-game overlay, or asserts live detection. Relay state is sender-reported advice, not independently verified observation.

## Result and release disposition

The implementation has narrow controls: one validated group-key header, per-room member limits, expiry sweep, server-assigned mark ids, separate admin credentials, and atomic-replace intent for state files. It does **not** provide authenticated member identity, replay/order protection, global mark-store bounds, request limiting, browser response hardening, or confidentiality against HTTP/direct relay compromise. The result is **not release-clear for a v2 relay that increases dependence on this transport**: High findings remain mitigation work; only sender-trust is explicitly accepted for the current advisory relay.

`#276`, `#278`, `#304`, and `#310` are future controls, not present behavior. Their mapping below must not be read as evidence that a request is currently protected.

## Endpoint and authority trace

`Program.cs` runs `RelayAccess.Refuses` before handlers. Once any room is registered, it returns 403 for an acceptable group key whose derived room is not allowlisted on `/state`, `/waypoints`, `/pings`, and `/report`; missing/invalid keys reach the handler and return 401. `GroupKey.RoomFor` derives a room from a reusable shared secret; it authenticates key possession, not a person.

| Route(s) | Current credential / role decision | State transition and bounds | Review result |
| --- | --- | --- | --- |
| `GET /health`, `/`, `/tablet`, `/catalog*`, `/landmarks`, `/search` | Public. | Health exposes aggregate counts; public catalog/search work consumes relay/upstream capacity. | Deliberate public data routing, but no application rate/concurrency limit. |
| `POST /state` | Exactly one acceptable `X-Group-Key`; closed relay requires a registered derived room. | Caller `name` (48 chars) is the member dictionary key; publish overwrites it. 16 members/room and 512 live state rooms. Reply excludes exact-name caller and sets `sinceSeconds`. | Key possession is room access, not member identity; full-room non-admission is returned as ordinary 200 state. |
| `GET /state` | Same group key and allowlist. | Read snapshot includes all members because tablet does not join. | Any key holder reads; no device/browser scope. |
| `DELETE /state/{name}` | Same group key and allowlist. | Removes any exact supplied name; always 200. | Key holder can evict a member; stock client handles its own rename correctly, server cannot bind name to caller. |
| `POST /waypoints`, `/pings` | Same group key and allowlist. | Validates `by`/map/finite coordinates; global monotonic ID; 60 waypoints or 30 pings **per room**, oldest eviction. | Ownerless by design; key holder may forge `by` and evict plan. |
| `POST /waypoints/{id}/reached`, `DELETE /waypoints/{id}`, `DELETE /waypoints` | Same group key and allowlist. | First completion wins; later duplicate complete is 404, remove/clear return 404/0. | No revision, ETag, idempotency or ownership; arrival order decides races. |
| `POST /report` | Same group key and allowlist. | 32 KiB Kestrel ceiling; 64 KiB UTF-8 check; three reports/derived-room/hour; persists body. | Rotating keys bypasses per-room limiter on open relay. |
| `GET /reports*`, `/admin/rooms*`, `/admin/update` | Separate `X-Admin-Key`; unset secret denies. `GET /admin` is public secret-free shell. | Registry max 64; remove unregisters then clears members; update writes marker file. | Correct handler-level separation, but bearer admin key lacks browser origin/CSRF/CSP defense. |

## State machines, ordering, and concurrency

| Area | Implemented transition / evidence | Invalid, duplicate, stale, or concurrent sequence | Residual risk and exact missing test |
| --- | --- | --- | --- |
| Member lifecycle | `GroupRooms.Publish` replaces a name; `Read` and one-minute `Sweep` expire after three minutes. `GroupSessionService` withdraws saved `(server,key,name)` on disable, unusable settings, rename, and dispose. | A publishes `Alice`; B with the key publishes `Alice`; B replaces A. A delayed `DELETE /state/Alice` after B's publish removes B. | **High.** Test collision, delayed delete after republish, case variants, rename in flight, and full-room admission response. Owner #304. |
| State reply ordering | POST publishes, then reads marks and members through separate stores/locks. Client applies every completed reply. | Send S1 then S2; S2 reply arrives first and renders; delayed S1 overwrites it. Concurrent mark mutation can fall between member and mark read. | **Medium.** Add client sequence and server room revision/snapshot contract; deterministic reversed-response and state+mark read tests. Owner #304. |
| Member capacity | Existing member can refresh at cap; new name hits `Count`/`ContainsKey` then `void` return. | Fill 16, send valid name 17: 200 response but peer never appears. Concurrent new names can cross non-atomic capacity check. | **Medium.** Atomic reservation and explicit capacity result; test 16/17 and concurrent admission. Owner #310. |
| Mark lifecycle | Per-room lock serializes add/read/complete/remove/clear; `Interlocked` IDs; first complete wins. | Duplicate POST creates another mark. Complete/delete/clear race resolves by arrival; stale tablet clears mark selected from old snapshot. | **Medium.** Revision/idempotency policy; tests for duplicate create, complete-vs-delete, clear-vs-add, returned revision. Owner #304/#310. |
| Expiry | Pings prune only on `Read`/later add; waypoint seven-day filter occurs only during load/restart. | Create ping then no reads/adds; it remains retained. Create waypoint, wait seven days without restart; it remains returned. | **Low retention; Medium aggregate DoS.** Timer expiry and empty-room removal; idle-expiry test without restart. Owner #310. |
| Persistence/restart | Waypoints/registry write a shared `.writing` file and move it; positions/pings do not persist. Load seeds id above restored max. Failures swallowed; bad registry clears all registrations. | Save snapshots are taken before `_saveGate`; later snapshot can write first, earlier stale snapshot can overwrite it. Corrupt `rooms.json` restarts as open relay. | **High registry authorization; Medium mark integrity.** Last-known-good/fail-safe registry, serialize snapshot+write in order, durable distinct-temp replacement; reordered-save/corrupt-registry/restart tests. Owner #310. |
| Admin registry | First registration closes relay; remove unregisters then `rooms.Clear`. `Adopt` accepts arbitrary nonempty `room`. | Remove final room while request passes middleware: state can publish after clear; empty registry makes relay open. Arbitrary text is stored as an allowlist room. | **Medium.** Define last-room policy and synchronize admission/registry; validate room-hash shape; test remove-vs-publish, last removal, invalid adoption. Owner #310. |

## Adversarial findings

All entries are **Reviewed** source evidence; the sequences are exact candidates for executable tests. “Mitigate” is not acceptance: a High finding remains open until code and exact-head test evidence exist.

| ID | Concrete failure sequence | Severity | Evidence / residual risk | Release disposition, owner, exact tests |
| --- | --- | --- | --- | --- |
| RELAY-AUTH-01 — shared-key impersonation and destructive leave | Obtain one valid key. After Alice publishes, send `POST /state {name:"Alice",...}` then `DELETE /state/Alice`, or clear/remove her marks. | **High** | Reviewed: `Program` handlers, `GroupRooms.Publish/Remove`, `GroupMarks`. `name`/`by` are unauthenticated text. | **Mitigate; blocks v2 expanded relay reliance. #304.** Test distinct session credentials, forged-name rejection, authenticated rename, and unauthorized leave/mutation rejection. |
| RELAY-AUTH-02 — key guessing and room oracle | Try likely >=8-char keys with `GET /state`; distinguish populated from empty on open relay and 403 from allowed on closed relay; on hit read/write room. | **High** | Reconciles `RISK-RELAY-KEY-BRUTEFORCE`; no limit/backoff and length does not establish entropy. | **Mitigate. #304/#310.** Test valid/invalid-key rate limits, generic non-oracular denial, generated-key entropy, rotation and single-device revoke. |
| RELAY-AUTH-03 — cleartext or relay-visible bearer credential | Open tablet/admin via `http://10.x.x.x:8090`, enter key, or operate a malicious HTTPS relay; capture header and replay it. | **High** | Reconciles `RISK-RELAY-KEY-DISCLOSURE`. Desktop permits private HTTP; browser inherits origin; receiver parses plaintext before hashing; tablet uses localStorage/admin uses sessionStorage. | **Mitigate. #304 transport/pairing; #310 operator.** Test non-loopback HTTP rejection/warning, HTTPS/HSTS deployment, chosen storage policy, scoped revocable credentials, malicious-relay disclosure. |
| RELAY-AUTH-04 — corrupt registry silently opens relay | Start closed with nonempty `rooms.json`; truncate/unread it; restart; post an invented acceptable key. | **High** | `GroupRoomRegistry.Load` clears list on exception; empty list means open. Does not reveal existing key, but silently changes operator authorization and enables abuse. | **Mitigate. #310.** Test last-known-good authorization or explicit maintenance mode, permissions, recovery alert. |
| RELAY-STATE-01 — stale/out-of-order replies | Send P1/P2 or M1/M2; force P2/M2 reply before P1/M1. No room/member revision or client sequence prevents older reply overwrite. | **Medium** | `Program` separately reads stores; `GroupSessionService.PublishOnceAsync` accepts every reply. | **Mitigate. #304.** Fake-handler reversed replies; integration tests for monotonic revision/conflict/retry on create/clear/reach/delete. |
| RELAY-STATE-02 — silent capacity non-admission | Populate 16 names; submit valid 17th `POST /state`. `Publish` returns but handler sends 200 room state. | **Medium** | `GroupRooms.MaximumMembersPerRoom`, void `Publish`, non-atomic count check. | **Mitigate. #310.** Explicit 429/409 policy; exactly-16 visibility, concurrent admission never exceeds cap, refresh at cap succeeds. |
| RELAY-STATE-03 — plan erasure/replay | Seed five waypoints; send 61 POSTs or replay captured POST. Oldest is evicted; retry creates new id. | **Medium** | Reconciles `RISK-RELAY-MARK-CAP`; per-room bound enables deterministic targeted erasure. | **Mitigate. #304/#310.** Per-actor quota/idempotency/revision, explicit eviction/audit, duplicate and clear/remove/reach authorization tests. |
| RELAY-STATE-04 — aggregate persistent mark DoS | On open relay choose N keys and post one waypoint each; each creates mark room and every waypoint save serializes all nonempty rooms. Wait >7 days without restart. | **Medium** | Reconciles `RISK-RELAY-MARK-CAP`; no global cap and expiry only on load. | **Mitigate. #310.** Global room/mark/serialized-byte caps, timer expiry, bounded write work, saturation denial test. |
| RELAY-BROWSER-01 — no CSRF/origin/CSP response hardening | `/tablet` and `/admin` set no CSP/frame/referrer/permissions/cache headers. Current custom-header fetch makes a cross-origin HTML form insufficient, but a same-origin injection/framed page can use stored key; server does not verify Origin/Fetch Metadata. | **Medium** | Reviewed: `Program`, HTML clients. `textContent` limits current reflected DOM XSS but is not a response policy. | **Mitigate. #310/#304.** Test nonce/hash CSP, `frame-ancestors 'none'`, nosniff, Referrer-Policy, Permissions-Policy, no-store credential pages/APIs, Origin/Sec-Fetch policy; add CSRF token if cookie auth arrives. |
| RELAY-OPS-01 — unbounded request/report/public work | Sustain public/group requests, or rotate keys through `/report`. Body cap limits each body, not request rate, room buckets, report count, or disk use. | **Medium** | Reconciles `RISK-RELAY-NO-RATE-LIMIT` / `RISK-REPORT-RATE-LIMIT`; Cloudflare does not protect direct LAN origin. | **Mitigate. #310.** Per-IP/global quota, bounded queue/concurrency, 429, report bucket expiry, global bytes/count/TTL, direct-LAN enforcement tests. |
| RELAY-TRUST-01 — fabricated freshness / malicious relay | Send old coordinates with `positionAge:0.1` every tick, or relay returns altered valid HTTPS JSON. | **Medium** | Reconciles accepted `RISK-RELAY-CLIENT-TRUST`; `sinceSeconds` proves only receipt timing. | **Accept only for current advisory relay; #317/#304 must restate.** Test sender/transport/replay provenance labelling and separate server freshness; never present as detection/verification. |

## Existing controls and test debt

| Narrow control | Reviewed evidence | Existing test names (not run) | Regression additions |
| --- | --- | --- | --- |
| Key shape/room hash | `GroupKey`, `Program.TryReadKey`. | `GroupKeyTests` | Multiple-header rejection and 8/128 boundary on every group route. |
| Closed-room middleware | `RelayAccess.IsGroupPath`. | `GroupRoomRegistryTests.EveryPathThatActsOnARoomIsGuarded` | Route-table coverage and registry transition concurrency. |
| Bounded state | 16 members/room, 512 rooms, three-minute sweep. | `GroupRoomsTests.RoomsAreCappedBeforeANewOneIsCreated`, `SweepingDropsStaleMembersAndThenTheEmptyRoom` | Atomic admission and HTTP semantics. |
| Mark locking/recovery | Per-room lock, IDs, persisted waypoint high-water mark, no ping persistence. | `GroupMarksTests` | Reordered saves, idle expiry, global limits, duplicate/mutation races. |
| Stock-client withdrawal | Saved registered identity used for DELETE. | `GroupWithdrawTests` | Delayed DELETE versus newer authenticated session; visible stale failure. |
| Admin separation | Separate secret/header and fixed-time comparison; keyed routes deny when unset. | No named direct admin authorization test located. | Every admin/report route, multi-header rejection, configured/unconfigured, browser header policy. |
| No obvious dynamic HTML injection | Both pages use `textContent`; no third-party CDN/script found. | `TabletPageTests.ItReachesNothingOutsideTheServerThatServedIt` | CSP/origin/storage/header tests above. |

## Reconciliation and future-control map

This lane supplies endpoint/state-machine detail behind the canonical security register; it does
not supersede it. Every `RELAY-*` finding has an inbound entry in
[`../PHASE_STATUS.md`](../PHASE_STATUS.md) and maps there to the stable abuse and risk IDs in
[`../ABUSE_CASES.md`](../ABUSE_CASES.md) and
[`../CONTROLS_AND_RESIDUAL_RISK.md`](../CONTROLS_AND_RESIDUAL_RISK.md). Equivalent authorization,
rate-limit, disclosure, and mark-cap scenarios share their existing canonical risks instead of
inflating the open-finding count.

| Existing finding / issue | Current fact | Future requirement — not implemented now |
| --- | --- | --- |
| `RISK-RELAY-IDENTITY`, `RISK-RELAY-KEY-BRUTEFORCE`, `RISK-GROUP-KEY-LOCAL-EXPOSURE` / **#304** | Shared reusable key plus caller display name; plaintext desktop settings/tablet localStorage; no member session. | #304 must define scoped/revocable device/member credentials, rotation, ordering, and authenticated confidential non-loopback transport. |
| `RISK-RELAY-KEY-DISCLOSURE` / **#276, #278, #304** | Private HTTP is permitted; tablet/admin use served scheme; relay sees header plaintext even under HTTPS. | #276/#278 must supply their product/security decisions and #304 must implement/test transport/pairing. Neither presently protects HTTP or blinds relay process. |
| `RISK-RELAY-MARK-CAP`, `RISK-RELAY-NO-RATE-LIMIT`, `RISK-REPORT-RATE-LIMIT` / **#310** | Per-room only caps, no live waypoint expiry, global snapshot write cost, no rate limiting. | #310 must implement global quotas, live expiry, persistence durability/fail-safe registry, mutation ordering/audit and admin/browser controls. |
| `RISK-RELAY-OBSERVED-DATA-POLICY`, `RISK-REPORT-REDACTION` / **#310** | `Observed` transmits while sharing and reports persist verbatim; this review does not relax `docs/SAFETY.md`. | #310 must prove end-to-end assembled/persisted minimization and consent/role behavior; existing release block remains. |
| `RISK-UPDATE-CHANNEL-TRUST` / **#317** | Update-marker authority is admin protected; artifact/update cryptography was out of this lane. | #317 retains ownership of release-chain evidence and acceptance; nothing here upgrades that tier. |

## Handoff criterion

Before a redesigned relay is release-ready, attach exact-head automated evidence for each named test, a passing GitHub Actions result for the substantive implementation, and a fresh route/state/browser trace after any #304/#310 replacement. Do not preserve a current-doc assertion merely because a future issue proposes a control.
