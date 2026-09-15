# Paired-device protocol 2.0

This is the normative desktop/tablet/relay contract for issues #276, #277, and #290. The paired
protocol starts at 2.0 and is separate from group relay protocol 1. Existing `GroupProtocol.Version`
and v1 group routes do not change. The schema is
`src/TarkovCompanion.CompanionProtocol/Schemas/v2/companion-protocol.schema.json`; the executable
contract and validation boundary are in `TarkovCompanion.CompanionProtocol`. The JSON files under
`tests/TarkovCompanion.CompanionProtocol.Tests/Golden` are canonical examples of every wire root,
and `Golden/crypto/paired-handshake-vectors.json` holds byte-exact handshake, key-schedule, and
relay vectors computed by an independent implementation of this document rather than by the C#
library.

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

Five boundaries are distinct:

| Boundary | Authority |
| --- | --- |
| Client JSON | May name a session, authority epoch, and command, but cannot assert its device identity, capabilities, server time, or delivery order. |
| Authenticated desktop adapter | Resolves the live session to a device key, device ID, negotiated version, capabilities, surface, and trusted receive time. |
| Canonical reducer | Applies a closed command to one aggregate or returns a typed rejection. It is transport-independent and never throws for hostile input. |
| Desktop identity key | A long-lived ECDSA P-256 key that signs every handshake transcript, so a tablet can tell the real desktop from a relay. |
| Hosted relay | Routes bounded encrypted frames and handshake messages. It is not canonical and cannot read paired state or impersonate either endpoint. |

`WorkspaceOrigin` from the v2 Core contract remains audit evidence, not authentication. Downstream
adapters build `AuthenticatedCommandContext` with `AuthenticatedCommandContext.ForPairedSession`
from a live `PairedDevice` and a live `DeviceSession` bound to the same `DeviceKeyId`; the factory
refuses a revoked, expired, or replaced device, an ended or expired session, or a key mismatch,
and grants only the intersection of device and session capabilities. The desktop's own UI uses a
context with `isDesktop: true` and the desktop device ID. Device roles (`Owner`, `Member`, and
`Observer`) and capability grants are authorization concepts. They do not encode a tactical role.

## Wire roots

`CompanionProtocolJson` reads and writes exactly these roots. A marker interface, arbitrary generic
type, CLR type name, or new record elsewhere does not become serializable by implementing or
resembling one of them.

| Root | Direction | Carried | Relay can read |
| --- | --- | --- | --- |
| `ClientHello`, `ServerHello` | tablet ↔ desktop | HTTPS, before a session | Version windows and recovery action |
| `PairingOffer` | desktop → tablet | HTTPS, after code resolution | Public identity key, public ephemeral key, nonce, attempt, expiry |
| `PairingRequest` | tablet → desktop | HTTPS | Public device and ephemeral keys, nonce, sealed name ciphertext and length |
| `SessionResumeRequest` | tablet → desktop | HTTPS | Device ID, key ID, credential ID, public ephemeral key, nonce |
| `HandshakeChallenge` | desktop → tablet | HTTPS | Session assignment, public keys, nonces, transcript hash, signature |
| `DeviceKeyProof` | tablet → desktop | HTTPS | WebAuthn assertion: credential ID, authenticator data (RP ID hash, flags, counter), client data (origin, challenge), signature |
| `SessionEstablished` | desktop → tablet | HTTPS | Session assignment and transcript hash |
| `ClientCommandEnvelope` | tablet → desktop | Inside `OpaqueRelayFrame` or direct TLS | Nothing when relayed |
| `ClientDeliveryAcknowledgement` | tablet → desktop | Inside `OpaqueRelayFrame` or direct TLS | Nothing when relayed |
| `ServerEnvelope` | desktop → tablet | Inside `OpaqueRelayFrame` or direct TLS | Nothing when relayed |
| `ReconnectRequest`, `ReconnectPlan` | tablet ↔ desktop | Inside `OpaqueRelayFrame` or direct TLS | Nothing when relayed |
| `OpaqueRelayFrame` | either | Relay HTTPS | Routing metadata listed below |

Handshake messages contain only public keys, nonces, identifiers, and signatures. The requested
device name is sealed; workspace, selection, capture, coordinate, mark, and authorization content
never appears in a handshake root.

JSON object members are unordered. A reader accepts a polymorphic `type` discriminator in any
position, so a browser client does not need to control property order.

## Pairing

Pairing is single-use, desktop-approved, mutually authenticated, and bound to a device key. The
C# model is `PairingStateMachine`; `Golden/handshake/pairing-*.json` is one complete run.

1. `ClientHello`/`ServerHello` fix the negotiated version before pairing.
2. The desktop creates a random pairing attempt with a five-minute expiry (ten minutes is the hard
   maximum), a fresh P-256 ECDH ephemeral key, and a 32-byte nonce. It displays a QR payload and a
   human short code. The QR payload carries the short code and the desktop identity key ID. The
   code is only a rate-limited lookup value: it travels in a dedicated redacted HTTPS header, never
   in JSON or a URL, and is never logged or placed in a fixture. The transport permits at most five
   attempts per source hash per five-minute window and keeps at most 160 observations.
3. Resolving the code consumes it and returns `PairingOffer`: attempt ID, desktop identity key,
   desktop ephemeral key, desktop nonce, and offer times. A tablet that scanned the QR code must
   refuse an offer whose identity key ID differs from the scanned one.
4. The tablet creates a platform-protected WebAuthn ES256 credential and its own ephemeral key and
   nonce, seals its requested display name (below), and sends `PairingRequest`. The desktop binds
   the first request to the attempt with `BindResolvedCode`; another request cannot replace that
   key. A request that reflects the desktop nonce or ephemeral key is refused.
5. Both screens show the six-digit verification code computed from the pairing commitment. The
   desktop approval prompt shows the opened device name, the device key fingerprint, and the code.
   The user approves only when the tablet shows the same code. A relay that substitutes either
   ephemeral key, either nonce, the device key, the credential, or the sealed name changes the
   code; a tablet that typed the short code has no other way to authenticate the desktop identity
   key, so the comparison is mandatory there.
6. On approval, `CreateChallenge` allocates `SessionAssignment` (negotiated version, device ID,
   session ID, relay channel ID, key epoch, cipher suite, session expiry of at most twelve hours),
   builds the transcript, and signs its hash with the desktop identity key. `Approve` accepts the
   challenge only if it reproduces the transcript of this offer and request.
7. The tablet rebuilds the transcript from its own offer and request with
   `HandshakeTranscript.FromChallenge`, requires `Matches`, and verifies the desktop signature
   against the identity key it authenticated in step 3 or 5. It then calls WebAuthn `get()` with
   the 32 raw transcript-hash bytes as the challenge, the bound credential ID in
   `allowCredentials`, and `userVerification: "required"`, and sends `DeviceKeyProof`.
8. `CompleteAsync` checks that the proof names this challenge before its expiry, uses the bound
   credential, carries user-present and user-verified flags, and has client data of type
   `webauthn.get` whose `challenge` is the base64url transcript hash and whose `crossOrigin` is
   absent or false. `IDeviceKeyProofVerifier` must then verify the ES256 signature over
   `authenticatorData ‖ SHA-256(clientDataJSON)` with the bound COSE key, the RP ID hash, the
   allowed origin, and a non-decreasing signature counter. Only then does the attempt reach
   `Completed` with `SessionEstablished`.
9. The desktop creates the `PairedDevice` and `DeviceSession` from `SessionEstablished`. Both sides
   derive traffic keys. A short code without local approval, a matching code, a valid desktop
   signature, and a valid device-key proof cannot complete pairing.

Private device material is outside the wire contract. The desktop identity key and desktop
private material are DPAPI-protected downstream. A browser keeps session traffic keys in memory
and uses the platform authenticator for reusable device proof; it may keep the desktop identity
public key, device ID, and credential ID, but it does not place a reusable bearer credential in
local storage, session storage, IndexedDB, a URL, JSON, diagnostics, or logs.

## Session resume and re-keying

A browser reload discards memory-only traffic keys. `SessionResumption` establishes a new session
for an already paired device without a new pairing attempt:

1. The tablet sends `SessionResumeRequest` with its device ID, device key ID, credential ID, a fresh
   ephemeral key, and a fresh nonce.
2. The desktop requires a live device (active, before absolute expiry, used within two hours) whose
   bound key and credential match, allocates a new `SessionAssignment` whose key epoch is greater
   than the device's previous one and whose expiry does not pass the device's, and signs a
   `SessionResume` transcript. The challenge lives at most two minutes.
3. The tablet verifies the transcript and signature against the identity key it stored at pairing,
   proves the challenge with WebAuthn exactly as in pairing step 7, and the desktop verifies it as
   in step 8.
4. The desktop ends the device's previous active session as `Replaced` and applies
   `ApplySessionTermination` to canonical state.

Sessions also re-key before a sender sequence would exceed 2³²−1 and whenever the device key is
replaced. A revoked, expired, or replaced device cannot resume.

## Handshake encodings

Every encoding below is a concatenation. `bytes(x)` is an unsigned 32-bit big-endian length
followed by `x`; `text(s)` is `bytes(UTF-8(s))`; `u16`, `u32`, `u64` are big-endian; `uuid(g)` is
the 16 RFC 9562 bytes in network order; `version(v)` is `u16(major) ‖ u16(minor)`; `instant(t)` is
a signed 64-bit big-endian Unix millisecond count. Base64url fields are unpadded, must use the
canonical spelling (unused trailing bits are zero), and are decoded to their exact bytes before
encoding. A key ID is `base64url(SHA-256(key bytes))`: the exact COSE_Key bytes for the device key
and the exact 91-byte uncompressed P-256 SubjectPublicKeyInfo for the desktop identity key.
Ephemeral keys are exactly that 91-byte SubjectPublicKeyInfo form.

**Pairing request context** (`PairingCryptography.EncodePairingRequestContext`):

| # | Field |
| --- | --- |
| 1 | `text("TarkovCompanion.PairedDevice/v2/pairing-request-context")` |
| 2 | `version(negotiated version)` |
| 3 | `uuid(attempt ID)` |
| 4 | `bytes(desktop identity key ID)` |
| 5 | `bytes(desktop ephemeral SPKI)` |
| 6 | `bytes(desktop nonce)` |
| 7 | `bytes(device key ID)` |
| 8 | `bytes(credential ID)` |
| 9 | `bytes(tablet ephemeral SPKI)` |
| 10 | `bytes(client nonce)` |
| 11 | `instant(offer expiry)` |

`C = SHA-256(context)`.

**Sealed device name.** `S` is the 32-byte P-256 ECDH secret (the shared point's x coordinate)
between the tablet ephemeral key and the desktop offer ephemeral key. The name is trimmed, 1–128
UTF-8 bytes, and contains no control or bidirectional-override characters. The key is
`HKDF-SHA-256(IKM = S, salt = C, info = UTF-8("TarkovCompanion.PairedDevice/v2/device-name-key"),
L = 32)`. `AES-256-GCM` uses a 12-byte zero nonce (the key is used once), additional data `C`, and a
16-byte tag. The ciphertext and tag are `SealedDeviceName`.

**Pairing commitment and verification code.**
`commitment = SHA-256(text("TarkovCompanion.PairedDevice/v2/pairing-commitment") ‖ bytes(C) ‖
bytes(name ciphertext) ‖ bytes(name tag))`. The code is the first four commitment bytes as an
unsigned big-endian integer, modulo 1,000,000, written as six decimal digits with leading zeros.

**Handshake transcript** (`HandshakeTranscript.Encode`):

| # | Field |
| --- | --- |
| 1 | `text("TarkovCompanion.PairedDevice/v2/handshake-transcript")` |
| 2 | `u16(purpose)`: 1 pairing, 2 session resume |
| 3 | `version(assignment protocol version)` |
| 4 | `uuid(challenge ID)` |
| 5 | `uuid(attempt ID)`, or 16 zero bytes for session resume |
| 6 | `bytes(pairing commitment)`, or `bytes()` for session resume |
| 7 | `uuid(assigned device ID)` |
| 8 | `bytes(device key ID)` |
| 9 | `bytes(credential ID)` |
| 10 | `bytes(desktop identity key ID)` |
| 11 | `u16(tablet ephemeral algorithm)` ‖ `bytes(tablet ephemeral SPKI)` |
| 12 | `u16(desktop ephemeral algorithm)` ‖ `bytes(desktop ephemeral SPKI)` |
| 13 | `bytes(client nonce)` |
| 14 | `bytes(desktop nonce)` |
| 15 | `uuid(session ID)` |
| 16 | `uuid(relay channel ID)` |
| 17 | `u16(cipher suite)` |
| 18 | `u32(key epoch)` |
| 19 | `instant(session expiry)` |
| 20 | `instant(challenge issued)` |
| 21 | `instant(challenge expiry)` |

`T = SHA-256(transcript)` is `transcriptHashBase64Url` and the raw WebAuthn challenge.

**Desktop signature.** ECDSA P-256 with SHA-256 over
`text("TarkovCompanion.PairedDevice/v2/desktop-handshake-signature") ‖ bytes(T)`, encoded as the
64-byte IEEE P1363 `r ‖ s` form WebCrypto produces. `HandshakeChallenge` verifies it on
construction, so a deserialized challenge is always authenticated by the identity key it names;
the tablet must still compare that key with the one it authenticated.

**Traffic keys.** With `S` the ECDH secret between the two ephemeral keys of the handshake,
`K_t2d = HKDF-SHA-256(IKM = S, salt = T, info = UTF-8("TarkovCompanion.PairedDevice/v2/tablet-to-desktop"), L = 32)` and
`K_d2t` uses `"TarkovCompanion.PairedDevice/v2/desktop-to-tablet"`. The labels are raw UTF-8 without
a length prefix. Pairing and session resume derive keys the same way; each session has its own
ephemeral keys, nonces, and `T`.

## End-to-end relay confidentiality

ADR 0009 selects end-to-end encryption for paired traffic. Direct-LAN and relayed transports feed
the same plaintext roots to the desktop, but the hosted relay receives only `OpaqueRelayFrame`.
The suite is P-256 ECDH, HKDF-SHA-256, and AES-256-GCM, chosen because current .NET and browser
WebCrypto implementations provide it without a protocol-specific native library.

`PairingCryptography.SealRelayFrame` and `OpenRelayFrame` are the executable definition:

- The plaintext is one UTF-8 JSON root of 1–65,536 bytes: a `ClientCommandEnvelope`,
  `ClientDeliveryAcknowledgement`, `ServerEnvelope`, `ReconnectRequest`, or `ReconnectPlan`.
- The nonce is `u32(key epoch) ‖ u64(sender sequence)`. The sender sequence starts at 1, increases
  by one per frame in each direction, and never exceeds 2³²−1. Each direction has its own key, so
  equal nonces in opposite directions never share a key.
- The additional authenticated data is `text("TarkovCompanion.PairedDevice/v2/relay-aad") ‖
  u16(direction) ‖ version(protocol version) ‖ uuid(channel ID) ‖ uuid(session ID) ‖
  u32(key epoch) ‖ u64(sender sequence) ‖ u16(cipher suite) ‖ u32(ciphertext length) ‖
  instant(issued) ‖ instant(expiry)`, where direction 1 is tablet-to-desktop and 2 is
  desktop-to-tablet. A receiver authenticates with the direction it expects, so a reflected frame
  fails.
- The ciphertext is written as base64url cut into 1,024-character chunks; only the last may be
  shorter. The frame's own JSON is bounded to 96 KiB, and a frame expires within five minutes.
- `RelayFrameReceiver` rejects a frame for another session, channel, or key epoch, an expired or
  far-future frame, and any sender sequence not greater than the last accepted one. It runs before
  decryption and advances only after authentication. Dropped frames are detected by the delivery
  stream, not repaired by the relay.

HTTPS remains required around the relay and direct gateway; end-to-end encryption does not replace
transport authentication, size limits, rate limits, or expiry. Failover never downgrades a paired
session to group protocol v1, a shared group key, cleartext HTTP, or an unauthenticated LAN
channel. If an authenticated confidential route is unavailable, the paired session fails closed
and reports the effective transport failure.

The relay privacy inventory is intentionally small:

- for frames: protocol major/minor, opaque channel ID, session routing ID, cipher suite, key epoch,
  sender sequence, ciphertext length, issue/expiry time, receipt time, and network metadata
  inherent to HTTPS;
- for handshakes: the public material listed in the wire-roots table, including the sealed name's
  ciphertext length;
- never: workspace, map, floor, viewport, selection, search, result, mark, coordinate, capture
  context, progress, correction, device display name, traffic key, or reusable authorization
  material.

The relay cannot mint canonical acknowledgements. The authenticated desktop assigns server UTC,
delivery sequence, device identity, revisions, and command results after decryption.

## Versioning and negotiation

Versions are `major.minor`, with major 1–99 and minor 0–999. The current paired protocol is 2.0.
A compatibility window covers one major. Peers select the highest minor in the intersection of
their windows and send only the selected version. `ServerHello` distinguishes compatible,
client-upgrade, desktop-upgrade, and no-shared-major outcomes. A compatible hello carries only a
negotiated version inside the desktop window; an incompatible one carries only a recovery action.

A command envelope must use exactly the session's negotiated version. A version the reader cannot
read, or a readable version that was not negotiated for the authenticated session, is
`UnsupportedVersion` and leaves canonical state untouched. There is no shared-version fallback
across majors.

A minor may add an optional object field or a reviewed closed-union member. Readers ignore unknown
optional object fields within a negotiated major. An unknown enum member, command discriminator,
workspace-action discriminator, update discriminator, server-message discriminator, or reconnect
disposition fails closed. Removing or renaming a field, adding a required field, changing
meaning/order/time/authentication semantics, changing a handshake encoding, or weakening a bound
or safety invariant requires a new major and an ADR.

A deprecation notice names the deprecated version (every minor up to it), a UTC sunset, a later
minimum replacement minor in the same major, a recovery action, and an explanation. Before sunset
a negotiated deprecated version carries the notice. From sunset, negotiation treats every minor
below the replacement as unsupported and answers with an upgrade outcome. Deprecation never
silently changes a command's meaning.

## Envelopes

`ClientCommandEnvelope` contains the negotiated protocol version, claimed session ID, the authority
epoch the command's revision was computed against, a diagnostic client UTC, and one closed
`CompanionCommand`. It has no origin device ID, device role, capabilities, server UTC, delivery
sequence, authorization grant, or credential field.

`ServerEnvelope` contains the negotiated version, session ID, authenticated origin device ID (the
device whose command caused the message, or the desktop), server UTC, positive per-device delivery
sequence, and one closed `ServerMessage`: a command acknowledgement, canonical snapshot, canonical
aggregate update, or deprecation notice.

`ClientDeliveryAcknowledgement` acknowledges the device delivery stream through one sequence and
reports the client's global revision and, per aggregate, its revision and applied change ID.

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

Each aggregate cursor holds its revision and the ID of the change occupying it (null exactly at
revision zero). Each command names the positive aggregate revision it intends to create: if the
current revision is `n`, a new command must request `n+1`. An applied change advances its own
aggregate revision and the global revision exactly once; an unrelated aggregate's revision never
makes a command stale. Each canonical update repeats the authority epoch.

Committed canonical state always fits the wire. After computing a change, the reducer serializes the
widest possible acknowledgement envelope carrying the full new state (`CanonicalDeliveryBudget`)
through the lexical boundary. A change whose state would exceed 64 KiB, a string bound, an array
bound, or the depth bound is rejected before commit, so an acknowledgement, snapshot, or update can
always be delivered. To keep that depth inside 16, a paired capture result carries at most three
provenance levels; the full #264 lineage stays with the referenced recognition result.

## Command acknowledgements

This section reconciles the paired acknowledgement with the acknowledgement rules of
`docs/V2_CONTRACT.md`. `AppliedRevision` is the aggregate revision the acknowledgement describes
and `AppliedChangeId` is the change occupying it, null exactly when that revision is zero. Comparing
`AppliedChangeId` with `CommandId` is what tells the command that landed from a divergent command
that lost. `CommandAcknowledgement` enforces the table on construction and when it is read:

| Disposition | V2 disposition | Revisions | `AppliedChangeId` | Canonical state |
| --- | --- | --- | --- | --- |
| `Applied` | `Applied` | applied = requested | this command | absent |
| `RejectedStale` | `RejectedStale` | applied > requested | another change | present |
| `RejectedConflict` | `RejectedConflict` | applied = requested > 0 | another change | present |
| `UnsupportedVersion` | `UnsupportedVersion` | current | not this command | absent |
| `RejectedExpired` | — | current | not this command | absent |
| `RejectedUnauthorized` | — | current | not this command | absent |
| `RejectedInvalidState` | — | current | not this command | absent |
| `RequiresPreview` | — | current | not this command | present |
| `RequiresSnapshot` | — | current | not this command | present |
| `RejectedCommandIdReuse` | — | current | whichever change occupies it | present |

When canonical state is present, its epoch, global revision, and the acknowledged aggregate's cursor
are exactly those the acknowledgement names.

The first four rows use the v2 disposition names with the v2 revision and change-ID rules;
`CommandAcknowledgement.CoreDisposition` maps them, and tests construct the Core
`StateAcknowledgement` from every such reducer acknowledgement. The paired conflict is narrower than
the v2 row: a command that jumps past the next revision, including past revision zero where v2 has
no conflicting change, is `RequiresSnapshot` rather than a conflict. The remaining rows are narrower
paired rejections that leave state untouched; none of them names the rejected command as the
applied change. A client acknowledgement of a canonical update is always a v2 `Applied` of a desktop
change, because a tablet never rejects canonical state; a tablet that cannot read an update
reconnects instead of acknowledging it.

Rejection reasons, in reducer order:

1. The envelope's session or desktop flag disagrees with the authenticated context: `RejectedUnauthorized`.
2. The version is unreadable or not the session's negotiated version: `UnsupportedVersion`.
3. A retained receipt exists for the command ID: an identical fingerprint from the same device is
   an idempotent `Applied` duplicate with code `duplicate-command`; anything else is
   `RejectedCommandIdReuse`.
4. The ID is a version-8 UUID, reserved for desktop maintenance changes, or already occupies an
   aggregate cursor: `RejectedCommandIdReuse`.
5. The command has expired, or was issued more than one minute in the desktop's future:
   `RejectedExpired` or `RejectedInvalidState`.
6. The context lacks the capability: `RejectedUnauthorized`.
7. The envelope names another authority epoch: `RequiresSnapshot`.
8. An offline preview is not bound to the current epoch and aggregate revision: `RequiresPreview`.
9. Revision below, equal to, or beyond the next: `RejectedStale`, `RejectedConflict`, `RequiresSnapshot`.
10. The transition itself is invalid, unauthorized, or would exceed the delivery budget.

**Idempotency.** A command ID is a change identity for the whole authority lifetime, not per
device. The desktop keeps a `RecentCommandReceipt` with the command's canonical fingerprint,
authenticated device, aggregate, applied revision, and expiry. A receipt is retained until its
command expires and, regardless of expiry, while its change still occupies its aggregate cursor, up
to the newest 256; pinned receipts are never evicted. So an exact retry of the newest change is
always recognized, and a retry whose receipt was evicted is evaluated as a new command whose
revision is already occupied, which can never apply twice. For a duplicate, `AppliedRevision` is
the revision the receipt proves the command occupies and the acknowledgement's global revision is
current.

The fingerprint is `CanonicalCommandFingerprint`: the command serialized through the closed
polymorphic model (discriminator, revision, lifetime, preview, and every field), then hashed as a
tagged binary tree with object members sorted by ordinal name, arrays in order, strings as unescaped
UTF-8, and numbers as the serializer's round-trip text. It does not depend on JSON member order or
escaping. Fingerprints are desktop-local and never cross the wire. Receipts are not part of the
serialized snapshot; #277 persists them beside canonical state.

The reducer returns a typed rejection for every hostile or malformed command. An `ArgumentException`,
`InvalidOperationException`, `UnauthorizedAccessException`, `OverflowException`, `JsonException`, or
`NotSupportedException` inside a transition becomes `RejectedInvalidState` or
`RejectedUnauthorized` with the unchanged state.

## Interaction state machine

`Follow`, `ControlPending`, `Control`, and `Independent` are persistent per-device states. A paired
device without an entry follows the desktop. The desktop is the authority and has no mode. Show on
desktop is a one-shot command and never a persistent mode.

| Current state | Input | Result |
| --- | --- | --- |
| Follow or Independent | Request Control | ControlPending; desktop approval is required. |
| ControlPending | Desktop approves | Control with a device/session-bound lease of at most five minutes; any old lease holder returns to Follow. |
| ControlPending | Desktop denies, request expires, requesting session ends, or device terminates | Follow; an existing lease holder keeps its lease. |
| Control | Lease expires, holding session ends, device terminates, or desktop preempts | Follow. |
| Follow, ControlPending, or Control | Enter Independent | Independent; any lease or pending request held by that device is released. |
| Independent | Enter Follow | Follow. |
| Independent | Session ends | Independent; local browsing continues and only explicit offline actions may be queued. |
| Independent | Show on desktop | Workspace changes once after revision/preview validation; device stays Independent. |
| Any | Device revoked, expired, or replaced | The entry is removed (Follow) and any lease or request is released. |

Only the desktop or a device with `ResolveControlRequests` may resolve a request, and a device can
never approve its own request. The desktop may preempt a lease at any time. Only the authenticated
holder of the active device/session-bound lease may submit `ControlWorkspaceCommand`. Control actions
are closed navigation, search, filter, selection, or explicitly authorized dialog actions. They are
not JSON Patch, arbitrary property paths, script, operating-system control, or EFT control.

Disconnect and revocation are enforced twice. The adapter calls `ApplySessionTermination` when a
session closes, is revoked, expires, or is replaced, and `ApplyDeviceTermination` when a device is
revoked, expires, or is replaced. Independently, `ApplyMaintenance(state, now, pairedDevices,
sessions)` runs at least hourly and on every lifecycle change; it releases any lease or pending
request whose session or device is no longer live and removes mode entries of terminated devices,
so a missed disconnect event cannot leave a device in Control.

**Desktop-local changes.** Navigation, search, selection, filter, and dialog changes made in the
desktop UI are `UpdateDesktopWorkspaceCommand`s applied through the same reducer with the desktop
context. They create ordinary workspace revisions and canonical updates, so tablets never miss a
local UI change and there is no second state-update path. A tablet cannot submit one, and it can
never be queued offline.

Independent browsing is local tablet state and has no wire command. It is never automatically
queued or replayed. `ShowOnDesktopCommand` is the only operation that transfers an independent
view to the canonical workspace, and the tablet remains Independent afterward. Because it replaces
the whole projection, it cannot open or dismiss a pairing-approval, revocation, multi-device
conflict, or team-member removal dialog unless the context holds `ManageDevices`; a general control
lease does not grant that capability. Selection carries an optional origin deep link and focus
token so the desktop can restore the intended companion focus without a desktop pixel coordinate.

## Coordinates and marks

`MapCoordinate` is either versioned world space or versioned normalized space. World x/y/z values
are finite and bounded to ±1,000,000. Normalized x/z values are in `[0,1]` and carry no height.
Every coordinate names a map, optional floor, and projection version. Desktop pixels are absent.
The map consumer validates the named map projection before applying a coordinate.

Every mark preserves a server-derived author device, per-mark revision, type, scope (`Private`,
`PairedDevice`, or `Team`), coordinate, label, `#RRGGBB` color, creation/update UTC, and optional
expiry. A tablet may edit or delete its own marks; desktop authority may repair them. Creating,
editing, or deleting a team-scoped mark requires `PublishTeamMarks` and downstream team opt-in. A
mark revision that disagrees with the cached one, or an edit of a missing mark, is
`RequiresSnapshot`. The protocol does not publish a device as a team member.

A ping lives at most 45 seconds from its creation, including after edits, and is never persistence
input. Waypoints, route points, and notes may persist. Server-time maintenance removes expired marks
as a revisioned change. Completion derived downstream from the user's own last-known position may
publish a final completion state to a team; no live position enters this paired-device contract.

## Contextual capture intent

The closed purposes are loot decision, full stash, ammo, keys, quest and future-quest items, map and
extracts, health and character, and auto-detect. They map to the frozen #264 `ScanIntent` values.
Flea recognition remains a valid v2 Core result, but it is not a paired-device capture purpose in
protocol 2.0.

An authorized desktop or tablet can submit `RequestCaptureIntentCommand` while no unexpired,
unfinished intent exists. The desktop validates the device/session/capability, aggregate revision,
queue preview, context, and expiry, then creates `ContextualCaptureIntent` with the authenticated
initiating device and surface. It expires within two minutes and carries a Core `CaptureSessionId`,
correlation ID, map/floor/profile context, previous-result reference, and bounded
objective/plan/mark references.

The desktop reports ordered progress for Armed, Awaiting user capture, Settling, Decoding, Detecting
context, Detecting regions, Matching, Enriching profile, Recommending, Awaiting review, and terminal
Complete/Cancelled/Failed stages. Progress preserves sequence, capture artifact and ordinal where
applicable, UTC, percent, and detail. A guided follow-up capture reported after a result keeps that
result awaiting review. Results reference the typed #264 recognition result by result/artifact ID
and capture ordinal and retain completeness, freshness, detected context, completion UTC, and at
most three provenance levels. A reviewed or corrected result cannot be replaced; re-recognition
starts a new intent. Guidance is a closed manual instruction such as take a user screenshot, open
the relevant panel, scroll for overlap, review ambiguity, or confirm the result. None performs the
instruction.

Review is Accepted or Needs correction and preserves reviewer device/time/note. Corrections are
append-only and ordered from one, with a closed correction kind, field ID, bounded corrected value,
reviewer device, UTC, and reason. They augment rather than replace the #264 evidence/correction
history. Only an accepted review makes the contextual capture Complete. Expiry, cancellation, or
failure after a result keeps that result as history.

## Delivery and backpressure

Every server envelope to a device consumes exactly one sequence of that device's single delivery
stream, in the order the desktop enqueues it, whatever the message. `DeliveryLedger` is the
executable model:

- Canonical updates enter the channel of their aggregate; acknowledgements, snapshots, and
  deprecation notices enter the control channel. Each channel of each device is bounded to 64
  pending deliveries.
- A full channel coalesces its pending deliveries into one snapshot-required marker at the next
  sequence. The transport sends a marker as a canonical snapshot of the state current when it is
  sent. Other channels of the device and every other device keep accepting deliveries, so a slow
  tablet cannot block unrelated state.
- `ClientDeliveryAcknowledgement` acknowledges the whole device stream through one sequence. The
  desktop refuses an acknowledgement from another authority epoch, one claiming a revision it does
  not hold or a different change at a revision it does hold, or one beyond the last assigned
  sequence. An old acknowledgement is ignored.

`CanonicalReplica` is the executable tablet reading of that stream and the reference #290 mirrors:

| Delivery | Replica result |
| --- | --- |
| Sequence at or below the last applied | Duplicate; nothing changes. |
| Canonical snapshot at any newer sequence | Replaces the cache and becomes the stream position. |
| Any other delivery while awaiting resync | Discarded. |
| Sequence other than last + 1 | Resync required: a delivery-sequence gap is never applied as a delta. |
| Update from another authority epoch | Resync required. |
| Update whose global revision is at or below the cache and whose aggregate revision is not newer | Already reflected (a snapshot contained it); the position advances. |
| Update whose global revision is not exactly the next, or whose aggregate revision is not exactly the next | Resync required. |
| Next update | Replaces that aggregate and advances the global revision. |
| Acknowledgement carrying newer or other-epoch canonical state | Replaces the cache. |
| Other acknowledgement or deprecation notice | Advances the position. |

A replica requiring resync sends `ReconnectRequest` on the live session.

## Reconnect and replay

A reconnect request sends the negotiated version, session, cached authority epoch (null after a
reload with no cache), last global revision, last contiguous delivery sequence, and one
acknowledgement per aggregate. `ReconnectPlanner` returns the plan and the ledger after handover:

- `UnsupportedVersion` when the request version cannot be read;
- `FullSnapshot` after an epoch change or missing cache, a claimed revision or change the desktop
  does not hold, a claimed sequence the desktop never assigned, a coalesced marker or acknowledged
  gap in the retained stream, more than 256 deliveries to replay, retained updates whose global
  revisions are not exactly contiguous to the current revision, or a replay plan that would exceed
  64 KiB;
- `UpToDate` when the client already holds the last assigned sequence and current global revision;
- `Replay` of the retained envelopes with their original sequences otherwise.

`ResumeAfterDeliverySequence` is the device's last assigned sequence; the client adopts it after
applying the plan and live delivery continues from the next sequence. Every plan except
`UnsupportedVersion` hands the stream over by acknowledging it through that sequence. A snapshot is
authoritative and atomically replaces the tablet's cache before deltas resume. Expired control or
capture commands never replay because commands are never replayed, only their canonical results.

## Offline actions

An offline tablet may cache the last canonical snapshot locally. Local Independent navigation,
search, filter, selection, control, and capture progress have no offline draft type and are never
queued. `OfflineActionQueue` holds at most 64 uniquely identified explicit drafts: Show on desktop,
mark upsert, mark delete, and capture-intent request. Each expires within fifteen minutes of
queueing and is pruned at expiry; it can never enter a later raid.

A draft is not a command. After reconnecting, the tablet shows each draft against the current
desktop state it just received, and the user previews, edits, or discards it. `PrepareSubmission`
then builds one command whose ID is the draft ID (so a retried submission is an idempotent
duplicate), whose issue time is the queue time, which requests the next revision of the previewed
state, and whose `OfflineQueuePreview` binds the approval to that state's authority epoch and
aggregate revision. Any intervening change produces `RequiresPreview`; the action is not rebased or
applied automatically, and the next draft for the same aggregate is previewed against the state
after the previous one landed. A command type that is not queue-eligible has no preview parameter,
so a preview smuggled in JSON is ignored rather than honored.

## Bounds and JSON validation

All transports call `CompanionProtocolJson` rather than default serializer options.

| Item | Bound |
| --- | --- |
| Plaintext root, including a full snapshot or reconnect plan | 64 KiB |
| Relay frame JSON | 96 KiB |
| Any UTF-8 string field | 1,024 bytes; identifiers use narrower limits where defined |
| Collection | 256 entries |
| JSON depth | 16; paired capture provenance is at most three levels |
| Paired devices | 32 |
| Marks | 256 |
| Recent idempotency receipts | 256 newest; a receipt whose change occupies a cursor is never evicted |
| Offline actions | 64, each for fifteen minutes |
| Delivery channel | 64 per device and channel |
| Replay | 256 deliveries |
| Timestamp | explicit UTC zero offset, millisecond precision |
| Client clock skew accepted for issue times | one minute |
| Pairing offer | five minutes normally, ten minutes maximum |
| Handshake challenge | pairing offer lifetime for pairing; two minutes for session resume |
| Session | twelve hours, and never past the device's expiry |
| Control lease | two minutes normally, five minutes maximum |
| Capture intent | two minutes |
| Device absence expiry | two hours unless explicitly revoked/replaced sooner |
| Sender sequence | 1 through 2³²−1 per session direction |
| Maintenance scan | at least hourly, with exact server-time expiry still enforced on use |

The lexical pass rejects oversize payloads and strings, invalid UTF-8, overlong arrays, excessive
depth, comments, trailing commas, case-insensitive duplicate keys, CLR type metadata, private-key,
secret, password, and authorization-like fields, the pairing short code, and non-JSON numeric
forms. Canonical options reject integer enum values, strings for numbers, missing constructor
parameters, null required references, and unknown discriminators. Constructors reject undefined enum
values, empty IDs, non-canonical base64url, key IDs that are not their key's thumbprint, malformed
public keys, unsigned challenges, non-finite/out-of-range coordinates, illegal timestamps, invalid
lifecycle combinations, inconsistent acknowledgements, and mutable-list substitution by copying
collections. Every such failure while reading a root surfaces as `JsonException`, so a transport
has exactly one rejection path.

Unknown optional object fields remain readable inside a negotiated major, but they do not bypass
the lexical security checks. Secrets and reusable authorization material are absent from every
wire root, schema, golden vector, log, URL, and diagnostic shape. The test-only private keys in the
crypto vectors are derived from public labels and protect nothing.

## Evidence

GitHub Actions runs these tests on every change; local runs are supplementary.

| Claim | Test |
| --- | --- |
| Every root has a golden vector that round-trips and validates strictly against the schema; every discriminator and enum in the schema matches the C# model | `GoldenAndHostileJsonTests` |
| Structured mutations of every golden vector fail only with `JsonException`; unknown optional fields from a newer minor stay readable; byte fuzz fails closed | `GoldenAndHostileJsonTests` |
| Handshake context, sealed name, commitment, code, transcripts, signature input, ECDH secrets, traffic keys, nonce, AAD, and the relay frame match an independent implementation | `CryptographyVectorTests` |
| Pairing and resume require approval, the code, a desktop signature, and a WebAuthn proof; relay substitution is detected | `HandshakeTests` |
| Acknowledgements satisfy the disposition table and the Core `StateAcknowledgement` rules; hostile command sequences never escape the reducer | `AcknowledgementContractTests` |
| Duplicates, mutated reuse, cross-device reuse, reserved IDs, and bounded receipts | `IdempotencyTests` |
| Desktop-local changes, disconnect/revoke/expiry to Follow, maintenance enforcement, context construction | `DeviceModeLifecycleTests`, `CanonicalStateMachineTests` |
| Delivery sequencing, isolation, gap detection, replica resync, and reconnect plans | `DeliveryAndReconnectTests` |
| The 64-action offline bound, expiry, preview binding, and eligibility | `OfflineActionQueueTests` |
| Negotiation and deprecation | `CompatibilityTests` |

## Consumer handoff

Issue #277 owns authentication adapters, protected credential storage, session persistence,
desktop approval UI orchestration, and transport composition. It must:

- keep the desktop identity key DPAPI-protected behind `IDesktopIdentitySigner`, and implement
  `IDeviceKeyProofVerifier` with the RP ID, origin, signature, and counter checks above;
- resolve short codes only from the redacted header, rate limit with `PairingRateLimiter`, and show
  the opened name, key fingerprint, and verification code in the approval prompt;
- construct authenticated context only with `ForPairedSession`, call `ApplySessionTermination`,
  `ApplyDeviceTermination`, and `ApplyMaintenance` as described, and end a device's previous session
  as `Replaced` after resume;
- persist canonical state and its receipts atomically, enqueue every returned acknowledgement and
  update through `DeliveryLedger`, resolve markers at send time, and hand the stream over with the
  ledger returned by `ReconnectPlanner`;
- run `RelayFrameReceiver` before opening each relay frame.

Issue #290 owns tablet presentation and local Independent state. It consumes the schema and golden
vectors, pins the desktop identity key from the QR code or verification code, displays the code,
mode/lease/security/conflict/expiry state, mirrors `CanonicalReplica` exactly, uses
`OfflineActionQueue` semantics for explicit drafts, and keeps reusable authorization material out of
browser storage. It cannot create a second JavaScript state-machine interpretation: server
acknowledgements, snapshots, and updates are authoritative.

The local gateway and hosted relay are transport adapters. They do not change authorization,
revision, conflict, expiry, projection, or capture meaning. Changes to a discriminator, bound,
state transition, handshake encoding, key schedule, relay-readable field, or canonical aggregate
require a protocol review and version/ADR decision.

This document and ADR define a protocol, not evidence that a future gateway or relay adapter is
securely implemented. As required by `docs/security/TBD_COMPONENTS.md`, #304 and downstream
composition work must add the implemented paired-device trust boundary, lost-device and
pairing-token abuse cases, failover/downgrade analysis, and tested controls to the living threat
model before release.
