# Paired-device protocol 2.0

This is the normative desktop/tablet/relay contract for issues #276, #277, and #290. The paired
protocol starts at 2.0 and is separate from group relay protocol 1. Existing `GroupProtocol.Version`
and v1 group routes do not change. The schema is
`src/TarkovCompanion.CompanionProtocol/Schemas/v2/companion-protocol.schema.json`; the executable
contract and validation boundary are in `TarkovCompanion.CompanionProtocol`, and the JSON files in
`tests/TarkovCompanion.CompanionProtocol.Tests/Golden` are canonical examples.

`AGENTS.md`, `docs/SAFETY.md`, and `docs/V2_CONTRACT.md` take precedence. This protocol narrows
their rules and does not grant a tablet or relay a game-facing capability.

## Scope and fixed safety boundary

The desktop is the sole authority over companion state. A paired tablet is another device owned
by the same user. It is not a group member, raid participant, player marker, or source of another
player's location. The protocol has no type for serializing it as any of those things.

The protocol can navigate the standalone companion, show companion data, create manual marks, and
arm companion recognition context. It cannot read or write Escape from Tarkov process memory,
inject or hook code, inspect or decode EFT traffic, generate gameplay input, automate inventory or
flea actions, detect or track enemies, produce ESP/radar, or draw an in-game overlay. A capture
intent arms recognition for the next user-created or otherwise permitted visible capture. Applying
an intent does not take a screenshot, press a key, click, move an item, or otherwise act on EFT.

## Trust model

Four boundaries are distinct:

| Boundary | Authority |
| --- | --- |
| Client JSON | May name a session and command, but cannot assert its device identity, server time, or delivery order. |
| Authenticated desktop adapter | Resolves the active session to a device key, device ID, capabilities, surface, and trusted receive time. |
| Canonical reducer | Applies a closed command to one aggregate or returns a typed rejection. It is transport-independent. |
| Hosted relay | Routes bounded encrypted frames. It is not canonical and cannot inspect paired state. |

`WorkspaceOrigin` from the v2 Core contract remains audit evidence, not authentication. Downstream
adapters must create `AuthenticatedCommandContext` from a live `DeviceSession` bound to the same
`DeviceKeyId`; they must never populate it from client JSON. Device roles (`Owner`, `Member`, and
`Observer`) and capability grants are authorization concepts. They do not encode a tactical role.

## Pairing and device keys

Pairing is single-use, desktop-approved, and bound to a device public key.

1. The desktop creates a random pairing attempt with a five-minute expiry. Ten minutes is the hard
   protocol maximum.
2. The desktop displays a QR payload and human short code. The code is only a rate-limited lookup
   value. It travels in a dedicated redacted HTTPS header, never in JSON or a URL, and is never
   logged or placed in a fixture.
3. A tablet generates a platform-protected WebAuthn ES256 credential and an ephemeral P-256 ECDH
   key. `PairingRequest` sends only their public material, a nonce, the requested display name, and
   the resolved attempt ID.
4. Resolving the code consumes it and binds the first request's `DeviceKeyId` to the attempt.
   Another request cannot replace that key. The transport permits at most five attempts per
   source hash per five-minute window.
5. The desktop shows the requested device name and key fingerprint and requires a local approval.
   Approval creates a challenge bound to the attempt, device key, both ephemeral keys, nonces, and
   negotiated version.
6. The tablet signs that challenge with its WebAuthn credential. `IDeviceKeyProofVerifier` must
   validate the assertion before `PairingStateMachine.CompleteAsync` reaches `Completed`.
7. The desktop creates the device and session only after completion. A short code without local
   approval and a valid device-key proof cannot complete pairing.

Private device material is outside the wire contract. Desktop implementations protect it with
Windows DPAPI. A browser keeps session traffic keys in memory and uses the platform authenticator
for reusable device proof; it does not place a reusable bearer credential in local storage,
session storage, IndexedDB, a URL, JSON, diagnostics, or logs.

`PairedDevice` has explicit `Active`, `Revoked`, `Expired`, and `Replaced` states. A replacement
names the new device ID and the old key does not follow it. `DeviceSession` has `Active`, `Closed`,
`Revoked`, `Expired`, and `Replaced` states and records its device/key binding, role-derived
capabilities, surface, transport, creation, last use, expiry, and end reason. Revocation, expiry,
and replacement terminate every session for that device; terminal device and session states do
not transition back to active.

## End-to-end relay confidentiality

ADR 0009 selects end-to-end encryption for paired traffic. Direct-LAN and relayed transports feed
the same plaintext envelopes to the desktop, but the hosted relay receives only
`OpaqueRelayFrame`. The suite is P-256 ECDH, HKDF-SHA-256, and AES-256-GCM, chosen because current
.NET and browser WebCrypto implementations provide it without a protocol-specific native library.

The key schedule binds the pairing attempt, negotiated version, device key thumbprint, both
ephemeral public keys, both nonces, session ID, and relay channel ID into the transcript hash.
HKDF derives separate tablet-to-desktop and desktop-to-tablet traffic keys. AES-GCM frames use a
12-byte nonce and 16-byte authentication tag. Key epoch and strictly increasing sender sequence
prevent nonce reuse; the frame metadata is authenticated as AEAD
additional data. Sessions re-key on reconnect, device-key replacement, or before a sequence could
repeat. HTTPS remains required around the relay and direct gateway; E2E encryption does not replace
transport authentication, size limits, rate limits, or expiry.
Failover never downgrades a paired session to group protocol v1, a shared group key, cleartext
HTTP, or an unauthenticated LAN channel. If an authenticated confidential route is unavailable,
the paired session fails closed and reports the effective transport failure.

The relay privacy inventory is intentionally small:

- protocol major/minor, opaque channel ID, session routing ID, cipher suite, key epoch, sender
  sequence, ciphertext length, frame expiry, receipt time, and network metadata inherent to HTTPS;
- no workspace, map, floor, viewport, selection, search, result, mark, coordinate, capture context,
  progress, correction, device display name, or reusable authorization material.

The relay cannot mint canonical acknowledgements. It only assigns relay delivery metadata and
forwards opaque frames. The authenticated desktop assigns paired-protocol server UTC, canonical
delivery sequence, device identity, revisions, and command results after decryption.

## Versioning and negotiation

Versions are `major.minor`, with major 1–99 and minor 0–999. The current paired protocol is 2.0.
A compatibility window covers one major. Peers select the highest minor in the intersection of
their windows and send only the selected version. A reader accepts its own major through its own
minor. There is no shared-version fallback across majors.

A minor may add an optional object field or a reviewed closed-union member. Readers ignore unknown
optional object fields within a negotiated major. An unknown enum, command discriminator, update
discriminator, or server-message discriminator fails closed. Removing or renaming a field, adding
a required field, changing meaning/order/time/authentication semantics, or weakening a bound or
safety invariant requires a new major and an ADR.

`ServerHello` distinguishes compatible, client-upgrade, desktop-upgrade, and no-shared-major
outcomes. An incompatible outcome carries an explicit recovery action. A deprecation notice names
the deprecated version, UTC sunset, minimum replacement, recovery action, and explanation.
Deprecation never silently changes a command's meaning. After sunset the version is unsupported.

## Envelopes and roots

Client and server roots are deliberately different.

`ClientCommandEnvelope` contains the negotiated protocol version, claimed session ID, client UTC,
and one closed `CompanionCommand`. It has no origin device ID, device role, server UTC, delivery
sequence, authorization grant, or credential field.

`ServerEnvelope` contains the negotiated version, session ID, authenticated origin device ID,
server UTC, positive per-device delivery sequence, and one closed `ServerMessage`. Server messages
are a command acknowledgement, canonical snapshot, canonical aggregate update, or deprecation
notice.

The exact JSON roots accepted by `CompanionProtocolJson` are client/server hello, pairing
request/challenge/proof, client command envelope, server envelope, opaque relay frame, reconnect
request, and reconnect plan. A marker interface, arbitrary generic type, CLR type name, or new
record elsewhere does not become serializable by implementing or resembling one of these roots.

## Canonical aggregates and revisions

One `AuthorityEpoch` identifies a continuous desktop authority lifetime. Desktop restart or
canonical-state replacement mints a new epoch. The state has a monotonic global revision for
reconnect coverage and four independently revisioned aggregates:

| Aggregate | Canonical content |
| --- | --- |
| Device modes | Per-device Follow/Control Pending/Control/Independent state, one pending request, and at most one control lease. |
| Workspace | Workspace, map/floor, versioned viewport, selection/deep link/focus token, visible objectives/plans, search/results, layers/filters, and allowed dialogs. |
| Marks | Manual pings, waypoints, route points, and notes with author, mark revision, scope, coordinate, color, times, and expiry. |
| Capture intent | One short-lived context with origin, purpose, session/correlation, progress, result reference, guidance, review, and corrections. |

Each command names the positive aggregate revision it intends to create. If current revision is
`n`, an ordinary new command must request `n+1`.

- The same authenticated device and still-retained command ID is an idempotent `Duplicate`; state
  does not change.
- A different command targeting an already occupied revision is `RejectedConflict`.
- A command targeting before the occupied revision is `RejectedStale`.
- A command jumping past the next revision is `RejectedConflict`.
- Stale, conflict, and preview-required acknowledgements include current canonical state and the
  current aggregate/global revisions. There is no last-write-wins path.
- An unrelated aggregate revision never makes this aggregate stale. Each applied change advances
  one aggregate revision and the global revision exactly once.
- Each canonical delta repeats the authority epoch; a tablet discards a delta from any prior
  desktop authority lifetime before considering its revisions.

Command IDs remain in a bounded 256-entry replay window through command expiry. Every command has
an explicit UTC issue/expiry interval: five minutes online or fifteen minutes for an approved
offline-queue action. An expired command is rejected and cannot enter a later raid.

## Interaction state machine

`Follow`, `ControlPending`, `Control`, and `Independent` are persistent per-device states. Show on
desktop is a one-shot command and never a persistent mode.

| Current state | Input | Result |
| --- | --- | --- |
| Follow or Independent | Request Control | ControlPending; desktop approval is required. |
| ControlPending | Desktop approves | Control with a device/session-bound expiring lease; any old lease is preempted. |
| ControlPending | Desktop denies or request expires | Follow. |
| Control | Lease expires, device disconnects/revokes, or desktop preempts | Follow. |
| Follow or Control | Enter Independent | Independent; any lease/pending request held by that device is released. |
| Independent | Enter Follow | Follow. |
| Independent | Show on desktop | Workspace changes once after revision/preview validation; device stays Independent. |

The desktop may preempt a control lease at any time. Only the authenticated holder of the active
device/session-bound lease may submit `ControlWorkspaceCommand`. Control actions are closed
navigation, search, filter, selection, or explicitly authorized dialog actions. They are not JSON
Patch, arbitrary property paths, script, operating-system control, or EFT control.

Independent browsing is local tablet state and has no wire command. It is never automatically
queued or replayed. `ShowOnDesktopCommand` is the only operation that transfers an independent
view to the canonical workspace, and the tablet remains Independent afterward. Selection carries
an optional origin deep link and focus token so the desktop can restore the intended companion
focus without carrying a desktop pixel coordinate.

Pairing approval, revocation, multi-device conflict, and team-member removal dialogs remain
desktop-only unless the device/session has the explicit administrative capability. A general
control lease does not grant that capability.

## Coordinates and marks

`MapCoordinate` is either versioned world space or versioned normalized space. World x/y/z values
are finite and bounded to ±1,000,000. Normalized x/z values are in `[0,1]` and carry no height.
Every coordinate names a map, optional floor, and projection version. Desktop pixels are absent.
The map consumer validates the named map projection before applying a coordinate.

Every mark preserves a server-derived author device, per-mark revision, aggregate revision, type,
scope (`Private`, `PairedDevice`, or `Team`), coordinate, label, `#RRGGBB` color, creation/update
UTC, and optional expiry. A tablet may edit or delete its own marks; desktop authority may repair
them. Team scope requires an explicit `PublishTeamMarks` capability and downstream team opt-in.
The protocol does not publish a device as a team member.

A ping has a required lifetime of at most 45 seconds and is never persistence input. Waypoints,
route points, and notes may persist. Server-time maintenance removes expired marks as a revisioned
change. Completion derived downstream from the user's own last-known position may publish a final
completion state to a team; no live position enters this paired-device contract.

## Contextual capture intent

The closed purposes are:

- Loot decision;
- Full stash;
- Ammo;
- Keys;
- Quest and future-quest items;
- Map and extracts;
- Health and character; and
- Auto-detect.

They map to the frozen #264 `ScanIntent` values. Flea recognition remains a valid v2 Core result,
but it is not a paired-device capture purpose in protocol 2.0.

An authorized desktop or tablet can submit `RequestCaptureIntentCommand`. The desktop validates
the device/session/capability, aggregate revision, queue preview, context, and expiry, then creates
`ContextualCaptureIntent` with the authenticated initiating device and surface. It expires within
two minutes and carries a Core `CaptureSessionId`, correlation ID, map/floor/profile context,
previous-result reference, and bounded objective/plan/mark references.

The desktop reports ordered progress for Armed, Awaiting user capture, Settling, Decoding,
Detecting context, Detecting regions, Matching, Enriching profile, Recommending, Awaiting review,
and terminal Complete/Cancelled/Failed stages. Progress preserves sequence, capture artifact and
ordinal where applicable, UTC, percent, and detail. Results reference the typed #264 recognition
result by result/artifact ID and capture ordinal and retain completeness, freshness, detected
context, completion UTC, and provenance.
Guidance is a closed manual instruction such as take a user screenshot, open the relevant panel,
scroll for overlap, review ambiguity, or confirm the result. None performs the instruction.

Review is Accepted or Needs correction and preserves reviewer device/time/note. Corrections are
append-only and ordered from one, with a closed correction kind, field ID, bounded corrected value,
reviewer device, UTC, and reason. They augment rather than replace the #264 evidence/correction
history. Only an accepted review makes the contextual capture Complete.

## Delivery, acknowledgements, and backpressure

Every server delivery has a positive sequence assigned by the desktop per receiving device.
Pending delivery is independently bounded per device and per aggregate to 64 items. A full channel
coalesces to one `snapshotRequired` marker. Other aggregates for the same device and every channel
for other devices continue accepting updates; a slow tablet cannot block unrelated state.

Acknowledgements apply through one sequence on one device/aggregate channel. An old acknowledgement
is ignored. A client also reports its last global revision and, for each known aggregate, the
revision, applied change ID, and client acknowledgement UTC. The change ID distinguishes an
idempotent copy from divergent state occupying the same revision; the timestamp is diagnostic and
never overrides desktop ordering.

## Reconnect, replay, and offline actions

A reconnect request sends negotiated version, device-key-bound session, authority epoch, last
global revision, last desktop delivery sequence, and bounded aggregate acknowledgements.

The desktop returns:

- `UpToDate` when epoch and revisions already match;
- `Replay` only when retained deliveries prove every global revision after the client's revision,
  in order and within 256 entries;
- `FullSnapshot` after epoch change, cursor disagreement, delivery gap, coalescing, or unavailable
  bounded history; or
- `UnsupportedVersion` with the handshake recovery path.

Expired control or capture commands never replay. A snapshot is authoritative and atomically
replaces the tablet's canonical cache before deltas resume.

An offline tablet may cache the last canonical snapshot locally. Local Independent navigation,
search, filter, and selection are never put into the reconnect command queue. Only an explicit
Show on desktop, mark upsert/delete, or capture-intent request may be retained, up to 64 items and
fifteen minutes. Before submission, the desktop shows each action against current state and the
user previews, edits, or discards it. The transmitted `OfflineQueuePreview` binds that approval to
the current epoch and aggregate revision. Any intervening change produces `RequiresPreview`; the
action is not rebased or applied automatically.

## Bounds and JSON validation

All transports call `CompanionProtocolJson` rather than default serializer options.

| Item | Bound |
| --- | --- |
| Plaintext aggregate update | 64 KiB |
| Any UTF-8 string field | 1,024 bytes; identifiers use narrower limits where defined |
| Collection | 256 entries |
| JSON depth | 16; coordinate objects are flat and may be nested at most two model levels |
| Paired devices | 32 |
| Marks | 256 |
| Recent idempotency receipts | 256 |
| Offline actions | 64 |
| Delivery channel | 64 per device/aggregate |
| Timestamp | explicit UTC zero offset, millisecond precision |
| Pairing offer | five minutes normally, ten minutes maximum |
| Control lease | two minutes normally, five minutes maximum |
| Capture intent | two minutes |
| Device absence expiry | two hours unless explicitly revoked/replaced sooner |
| Maintenance scan | at least hourly, with exact server-time expiry still enforced on use |

The lexical pass rejects oversize payloads and strings, overlong arrays, excessive depth,
case-insensitive duplicate keys, CLR type metadata, private-key/password/authorization-like fields,
the pairing short code, and non-JSON numeric forms. Canonical options reject integer enum values,
missing constructor parameters, null required references, and unknown discriminators. Constructors
reject undefined enum values, empty IDs, non-finite/out-of-range coordinates, illegal timestamps,
invalid lifecycle combinations, and mutable-list substitution by copying collections.

Unknown optional object fields remain readable inside a negotiated major, but they do not bypass
the lexical security checks. Secrets and reusable authorization material are absent from every
wire root, schema, golden vector, log, URL, and diagnostic shape.

## Consumer handoff

Issue #277 owns authentication adapters, protected credential storage, session persistence,
desktop approval UI orchestration, and transport composition. It must construct authenticated
context from the verified session/device-key record, run server-time maintenance, call the reducer,
persist the returned canonical state atomically, and publish the returned update through isolated
delivery channels.

Issue #290 owns tablet presentation and local Independent state. It consumes the schema and golden
vectors, displays mode/lease/security/conflict/expiry state, acknowledges canonical revisions, and
keeps reusable authorization material out of browser storage. It cannot create a second JavaScript
state-machine interpretation: server acknowledgements, snapshots, and updates are authoritative.

The local gateway and hosted relay are transport adapters. They do not change authorization,
revision, conflict, expiry, projection, or capture meaning. Changes to a discriminator, bound,
state transition, key schedule, relay-readable field, or canonical aggregate require a protocol
review and version/ADR decision.

This document and ADR define a protocol, not evidence that a future gateway or relay adapter is
securely implemented. As required by `docs/security/TBD_COMPONENTS.md`, #304 and downstream
composition work must add the implemented paired-device trust boundary, lost-device and
pairing-token abuse cases, failover/downgrade analysis, and tested controls to the living threat
model before release.
