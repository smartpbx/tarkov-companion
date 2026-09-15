# Paired-device protocol 2.0

This is the normative desktop/tablet/relay contract for issues #276, #277, and #290. The paired
protocol starts at 2.0 and is separate from group relay protocol 1. Existing `GroupProtocol.Version`
and v1 group routes do not change. The schema is
`src/TarkovCompanion.CompanionProtocol/Schemas/v2/companion-protocol.schema.json`; the executable
contract and validation boundary are in `TarkovCompanion.CompanionProtocol`. The JSON files under
`tests/TarkovCompanion.CompanionProtocol.Tests/Golden` are canonical examples of every wire root,
and `Golden/crypto/paired-handshake-vectors.json` holds byte-exact handshake, key-schedule, relay,
and transport-binding vectors computed by an independent implementation of this document rather
than by the C# library.

`AGENTS.md`, `docs/SAFETY.md`, and `docs/V2_CONTRACT.md` take precedence. This protocol narrows
their rules and does not grant a tablet or relay a game-facing capability. `docs/V2_CONTRACT.md`
("Paired-device transport") names this document as the governed paired-device transport and
canonical-state contract; the paired model reuses the Core v2 DTOs described below instead of
forking them.

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
| Authenticated desktop adapter | Opens each frame with a live session's traffic keys and resolves that session to a device key, device ID, negotiated version, capabilities, surface, client instance, and trusted receive time. |
| Canonical reducer | Applies a closed command to one aggregate or returns a typed rejection. It is transport-independent and never throws for hostile input. |
| Desktop identity key | A long-lived ECDSA P-256 key that signs every handshake transcript, so a tablet can tell the real desktop from a relay. |
| Hosted relay | Routes bounded encrypted frames and public handshake messages. It is not canonical and cannot read paired state or impersonate either endpoint. |

`WorkspaceOrigin` from the v2 Core contract remains audit evidence, not authentication. A session is
authenticated by its traffic keys, never by a session ID a peer presents. Downstream adapters build
`AuthenticatedCommandContext` with `AuthenticatedCommandContext.ForPairedSession(device, session,
clientInstanceId, receivedUtc)` only after `RelayFrameReceiver` and `OpenRelayFrame` accept a frame
under that session's keys. The factory refuses a revoked, expired, or replaced device, an ended or
expired session, or a key mismatch, and grants only the intersection of device and session
capabilities. The client instance is the `ClientHello.ClientInstanceId` of the connection that
established the session. The desktop's own UI uses a context with `isDesktop: true`, the desktop
device ID, and the desktop instance. Device roles (`Owner`, `Member`, and `Observer`) and capability
grants are authorization concepts. They do not encode a tactical role.

## Wire roots

`CompanionProtocolJson` reads and writes exactly these roots. A marker interface, arbitrary generic
type, CLR type name, or new record elsewhere does not become serializable by implementing or
resembling one of them.

| Root | Direction | Carried | Relay can read |
| --- | --- | --- | --- |
| `ClientHello`, `ServerHello` | tablet ↔ desktop | Plaintext message, before a session | Version windows, client instance, and recovery action |
| `PairingOffer` | desktop → tablet | Plaintext, after code resolution | Public identity key, public ephemeral key, desktop nonce commitment, attempt, times |
| `PairingRequest` | tablet → desktop | Plaintext | Public device and ephemeral keys, nonce, sealed name ciphertext and length |
| `PairingNonceReveal` | desktop → tablet | Plaintext, after the request is bound | Attempt and desktop nonce |
| `SessionResumeRequest` | tablet → desktop | Plaintext | Device ID, key ID, credential ID, public ephemeral key, nonce |
| `HandshakeChallenge` | desktop → tablet | Plaintext | Session assignment, public keys, nonces, transcript hash, signature |
| `DeviceKeyProof` | tablet → desktop | Plaintext | WebAuthn assertion: credential ID, authenticator data (RP ID hash, flags, counter), client data (origin, challenge), signature |
| `SessionEstablished` | desktop → tablet | Plaintext | Session assignment and transcript hash |
| `ClientCommandEnvelope` | tablet → desktop | Only inside `OpaqueRelayFrame`, on every transport | Nothing |
| `ClientDeliveryAcknowledgement` | tablet → desktop | Only inside `OpaqueRelayFrame`, on every transport | Nothing |
| `ReconnectRequest` | tablet → desktop | Only inside `OpaqueRelayFrame`, on every transport | Nothing |
| `ServerEnvelope` | desktop → tablet | Only inside `OpaqueRelayFrame`, on every transport | Nothing |
| `ReconnectPlan` | desktop → tablet | Only inside `OpaqueRelayFrame`, on every transport | Nothing |
| `OpaqueRelayFrame` | either | Any transport | Routing metadata listed below |

`PairedTransportBinding.PlaintextRoots` and `PairedTransportBinding.FramedRoots` are the executable
form of the "Carried" column. Handshake messages contain only public keys, nonces, identifiers, and
signatures. The requested device name is sealed; workspace, selection, capture, coordinate, mark,
and authorization content never appears in a handshake root.

JSON object members are unordered. A reader accepts a polymorphic `type` discriminator in any
position, so a browser client does not need to control property order.

## Pairing

Pairing is single-use, desktop-approved, mutually authenticated, and bound to a device key. The
C# model is `PairingStateMachine`; `Golden/handshake/pairing-*.json` is one complete run.

1. `ClientHello`/`ServerHello` fix the negotiated version before pairing.
2. The desktop creates a random pairing attempt with a five-minute expiry (ten minutes is the hard
   maximum), a fresh P-256 ECDH ephemeral key, and a 32-byte desktop nonce from the operating-system
   CSPRNG, and displays a QR
   payload and a ten-symbol pairing code (see Transport binding). The code is only a rate-limited
   lookup value: it travels in the redacted `Tarkov-Pairing-Code` header, never in JSON or a URL,
   and a live code is never logged or placed in a fixture (the vectors use a fixed test value).
3. Resolving the code consumes it and returns `PairingOffer`: attempt ID, desktop identity key,
   desktop ephemeral key, the commitment `SHA-256(text("TarkovCompanion.PairedDevice/v2/desktop-nonce-commitment")
   ‖ bytes(desktop nonce))`, and offer times. The nonce itself stays on the desktop. A tablet that
   scanned the QR code must refuse an offer whose identity key ID differs from the scanned one.
4. The tablet creates a platform-protected WebAuthn ES256 credential and its own ephemeral key and
   nonce, seals its requested display name (below), and sends `PairingRequest`. It creates exactly
   one request per offer and resends that identical request on a transport retry; a new request
   needs a new pairing code, so a relay cannot collect several requests against one nonce. The desktop binds
   the first request to the attempt with `BindResolvedCode`, which also requires the request's
   version to be the connection's negotiated version; another request cannot replace that key. A
   request that reflects the desktop nonce or ephemeral key is refused.
5. Only now does `RevealNonce` release `PairingNonceReveal`. The tablet checks the nonce against the
   offer's commitment (`PairingCryptography.IsRevealOf`) and refuses to show a code otherwise.
6. Both screens show the six-digit verification code computed from the pairing commitment, which
   covers the request and the revealed nonce. The desktop approval prompt shows the opened device
   name, the device key fingerprint, and the code, and requires the user to confirm that the tablet
   shows the same code before approval is possible. The comparison is mandatory in both flows: a
   scanned QR payload lets the tablet authenticate the desktop, but only the compared code lets the
   desktop authenticate the tablet's request, because whoever resolves the code, including a relay
   that forwards the lookup, can bind the first request. Whoever chooses a request fixed it before the
   nonce was known, so a substituted request matches the tablet's code with probability one in a
   million per attempt, and a failed attempt consumes the one-time code.
7. On approval, `CreateChallenge` allocates `SessionAssignment` (negotiated version, device ID,
   session ID, relay channel ID, key epoch, cipher suite, session expiry of at most twelve hours),
   builds the transcript, and signs its hash with the desktop identity key. `Approve` accepts the
   challenge only if it reproduces the transcript of this offer, request, and nonce.
8. The tablet rebuilds the transcript from its own offer, request, and reveal with
   `HandshakeTranscript.FromChallenge`, requires `Matches`, and verifies the desktop signature
   against the identity key it authenticated in step 3 or 6. It then calls WebAuthn `get()` with
   the 32 raw transcript-hash bytes as the challenge, the bound credential ID in
   `allowCredentials`, and `userVerification: "required"`, and sends `DeviceKeyProof`.
9. `CompleteAsync` checks that the proof names this challenge before its expiry, uses the bound
   credential, carries user-present and user-verified flags, and has client data of type
   `webauthn.get` whose `challenge` is the base64url transcript hash and whose `crossOrigin` is
   absent or false. `IDeviceKeyProofVerifier` must then verify the ES256 signature over
   `authenticatorData ‖ SHA-256(clientDataJSON)` with the bound COSE key, the pinned RP ID hash,
   the pinned tablet origin, and a non-decreasing signature counter. Only then does the attempt
   reach `Completed` with `SessionEstablished`.
10. The tablet accepts `SessionEstablished` only if `Answers(challenge)` holds for the challenge it
    verified. The desktop creates the device with `DeviceLifecycle.Pair`, which records the first
    key epoch, and the `DeviceSession` from `SessionEstablished`. Both sides derive traffic keys. A
    short code without local approval, a matching code, a valid desktop signature, and a valid
    device-key proof cannot complete pairing.

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
2. `SessionResumption.Begin` requires a live device (active, before absolute expiry, used within two
   hours) whose bound key and credential match, a request in the connection's negotiated version,
   and a new `SessionAssignment` whose key epoch is greater than `PairedDevice.LastKeyEpoch` and
   whose expiry does not pass the device's. It signs a `SessionResume` transcript and returns a
   `SessionResumeAttempt` that the desktop persists by challenge ID. The challenge lives at most two
   minutes.
3. The tablet rebuilds the transcript with `HandshakeTranscript.FromChallenge(challenge, request,
   pinnedDesktopIdentityKey)`, using the identity key it pinned at pairing rather than the key the
   challenge names, proves it with WebAuthn exactly as in pairing step 8, and accepts
   `SessionEstablished` only if it `Answers` that challenge.
4. `SessionResumption.CompleteAsync` verifies the proof as in pairing step 9 and moves the attempt to
   `Completed`. A completed or expired attempt never establishes another session.
5. The desktop records the session with `DeviceLifecycle.RecordSession`, which raises
   `LastKeyEpoch` and returns the updated device. It then separately ends the device's previous
   active session with `DeviceLifecycle.EndSession(..., Replaced, ...)` and applies
   `ApplySessionTermination` with that ended session to canonical state.

Because every established session raises the device's key epoch, a captured proof for that or any
earlier epoch cannot re-establish a session even if the attempt store is lost, so a relay cannot
replay a resume to reset a receiver's replay window. Sessions also re-key before a sender sequence
would exceed 2³²−1 and whenever the device key is replaced. A revoked, expired, or replaced device
cannot resume. `DeviceLifecycle.RecordUse` refreshes the device's and session's last use for every
accepted frame, so only real absence reaches the two-hour inactivity expiry.

## Handshake encodings

Every encoding below is a concatenation. `bytes(x)` is an unsigned 32-bit big-endian length
followed by `x`; `text(s)` is `bytes(UTF-8(s))`; `u16`, `u32`, `u64` are big-endian; `uuid(g)` is
the 16 RFC 9562 bytes in network order; `version(v)` is `u16(major) ‖ u16(minor)`; `instant(t)` is
a signed 64-bit big-endian Unix millisecond count. Base64url fields are unpadded, must use the
canonical spelling (unused trailing bits are zero), and are decoded to their exact bytes before
encoding. A key ID is `base64url(SHA-256(key bytes))`: the exact COSE_Key bytes for the device key
and the exact 91-byte uncompressed P-256 SubjectPublicKeyInfo for the desktop identity key.
Ephemeral and identity keys are exactly that SubjectPublicKeyInfo form, and their point must lie on
the curve. The device key is exactly the 77-byte CTAP2 canonical COSE_Key
`{1: 2, 3: -7, -1: 1, -2: x, -3: y}` (EC2, ES256, P-256) with its point on the curve.

**Desktop nonce commitment** (`PairingCryptography.ComputeDesktopNonceCommitment`):
`N_c = SHA-256(text("TarkovCompanion.PairedDevice/v2/desktop-nonce-commitment") ‖ bytes(desktop nonce))`.

**Pairing request context** (`PairingCryptography.EncodePairingRequestContext`):

| # | Field |
| --- | --- |
| 1 | `text("TarkovCompanion.PairedDevice/v2/pairing-request-context")` |
| 2 | `version(negotiated version)` |
| 3 | `uuid(attempt ID)` |
| 4 | `bytes(desktop identity key ID)` |
| 5 | `bytes(desktop ephemeral SPKI)` |
| 6 | `bytes(N_c)` |
| 7 | `bytes(device key ID)` |
| 8 | `bytes(credential ID)` |
| 9 | `bytes(tablet ephemeral SPKI)` |
| 10 | `bytes(client nonce)` |
| 11 | `instant(offer expiry)` |

`C = SHA-256(context)`.

**Sealed device name.** `S` is the 32-byte P-256 ECDH secret (the shared point's x coordinate)
between the tablet ephemeral key and the desktop offer ephemeral key. The name is trimmed, 1–128
UTF-8 bytes, and contains no Unicode control, format (zero-width, joiner, bidirectional), line or
paragraph separator, or private-use character. The key is
`HKDF-SHA-256(IKM = S, salt = C, info = UTF-8("TarkovCompanion.PairedDevice/v2/device-name-key"),
L = 32)`. `AES-256-GCM` uses a 12-byte zero nonce (the key is used once), additional data `C`, and a
16-byte tag. The ciphertext and tag are `SealedDeviceName`.

**Pairing commitment and verification code.**
`commitment = SHA-256(text("TarkovCompanion.PairedDevice/v2/pairing-commitment") ‖ bytes(C) ‖
bytes(name ciphertext) ‖ bytes(name tag) ‖ bytes(desktop nonce))`, computed only with a nonce that
opens `N_c`. The code is the first four commitment bytes as an unsigned big-endian integer, modulo
1,000,000, written as six decimal digits with leading zeros.

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
the tablet must still compare that key with the one it authenticated or pinned.

**Traffic keys.** With `S` the ECDH secret between the two ephemeral keys of the handshake,
`K_t2d = HKDF-SHA-256(IKM = S, salt = T, info = UTF-8("TarkovCompanion.PairedDevice/v2/tablet-to-desktop"), L = 32)` and
`K_d2t` uses `"TarkovCompanion.PairedDevice/v2/desktop-to-tablet"`. The labels are raw UTF-8 without
a length prefix. Pairing and session resume derive keys the same way; each session has its own
ephemeral keys, nonces, and `T`.

## Transport binding

`PairedTransportBinding` is the executable binding shared by the desktop LAN gateway (#277), the
hosted relay, and the tablet (#290).

**One framing on every transport.** The LAN gateway and the relay expose the same WebSocket path,
`/v2/companion/connect`. Each text message carries exactly one UTF-8 JSON root. Before a session,
only the hello and handshake roots may appear, in the order of the Pairing or Session resume
section. After `SessionEstablished`, a connection carries only `OpaqueRelayFrame` for that session,
opened with its traffic keys after `RelayFrameReceiver` accepts the frame; any other root closes the
connection. There is no plaintext command, acknowledgement, update, or reconnect path, on the LAN or
through the relay. TLS (`wss://`, `https://`) is an outer layer that protects metadata and never
substitutes for frame authentication. A direct LAN route whose certificate the tablet browser does
not trust is unavailable, and the tablet uses the relay; it never falls back to cleartext.

**Pairing code and offer.** The pairing code is ten Crockford base32 symbols
(`0123456789ABCDEFGHJKMNPQRSTVWXYZ`, 50 bits) from the operating-system CSPRNG
(`GeneratePairingCode`). A typed code is normalized by ignoring spaces and hyphens, folding case,
and reading `O` as `0` and `I` or `L` as `1` (`NormalizePairingCode`). The tablet resolves it with
`POST /v2/companion/pairing/offer` carrying the code only in the `Tarkov-Pairing-Code` header; the
answer is `PairingOffer` JSON. Servers redact that header from every log and trace. A relay forwards
the lookup to the desktop registered for the code and learns only what the code already reveals.

**QR payload.** `TARKOV-COMPANION-PAIR/2.0/<code>/<desktop identity key ID>` (`FormatQrPayload`,
`TryParseQrPayload`). It is read by the tablet application's own scanner and is not a navigable URL,
so the code never enters browser history.

**Rate limiting.** The desktop keys `PairingRateLimiter` with
`base64url(HMAC-SHA-256(K, text("TarkovCompanion.PairedDevice/v2/pairing-source") ‖ bytes(source)))`
(`ComputeSourceHash`), where `K` is at least 32 random bytes generated at desktop start and the
source is the IPv4 address, or the first 64 bits of an IPv6 address, with IPv4-mapped IPv6 read as
IPv4. At most five attempts per source hash fit a five-minute window, and the limiter keeps at most
160 observations; when that window is full it refuses every new attempt until an observation ages
out rather than evicting history. Behind the relay the desktop sees only the relay, so the relay
applies the same limiter keyed by the tablet's source with its own key before forwarding a lookup,
and the desktop keys lookups forwarded by the relay with the relay connection as one source. The
160-observation window is therefore also a global bound on code guesses per five minutes.

**WebAuthn relying party and tablet origin.** The tablet application is served as static files from
one deployment-pinned HTTPS origin that is not the relay API. The WebAuthn RP ID is that origin's
registrable host, and `IDeviceKeyProofVerifier` pins both the RP ID hash and the exact origin; an
IP-address origin cannot use WebAuthn and is not supported. The vectors use the reserved name
`companion.example`.

**Relay channels.** The desktop registers each session's relay channel ID with the relay over its
own authenticated outbound connection when the session is established, and removes it when the
session ends. The relay forwards frames between the desktop connection and tablet connections that
name that channel. Knowing a channel ID lets a party send frames that fail authentication and
consume rate limits; it never lets a party read or inject state.

**Clocks.** Frame and command lifetimes use the sender's clock and are checked against the desktop
clock with one minute of tolerated skew. A tablet estimates its offset from `ServerEnvelope.ServerUtc`
(the median of recent deliveries) and issues commands and frames on the corrected clock; a tablet
whose offset exceeds the tolerance shows a clock warning instead of retrying.

**Residual risk.** End-to-end encryption protects paired state from the relay process, its logs, and
its operators. It does not protect against a compromised tablet application origin, which can serve
code that exfiltrates state inside the browser; that origin is part of the trusted computing base,
is deployed separately from the relay, and must be protected like a release-signing key. A relay
that substitutes a typed-code pairing request succeeds if the user approves without comparing the
verification code, whether the tablet scanned the QR payload or typed the code, and otherwise with
probability one in a million per attempt, each attempt consuming a code. Relay-visible metadata
remains visible.

## End-to-end relay confidentiality

ADR 0009 selects end-to-end encryption for paired traffic, and the transport binding applies it to
every route. The suite is P-256 ECDH, HKDF-SHA-256, and AES-256-GCM, chosen because current .NET and
browser WebCrypto implementations provide it without a protocol-specific native library.

`PairingCryptography.SealRelayFrame` and `OpenRelayFrame` are the executable definition:

- The plaintext is `u16(payload kind) ‖ UTF-8 JSON root`, where the root is 1–65,536 bytes, so the
  ciphertext is 3–65,538 bytes. The kind is authenticated and hidden from the relay:

  | Kind | Root | Direction |
  | --- | --- | --- |
  | 1 | `ClientCommandEnvelope` | tablet-to-desktop |
  | 2 | `ClientDeliveryAcknowledgement` | tablet-to-desktop |
  | 3 | `ReconnectRequest` | tablet-to-desktop |
  | 4 | `ServerEnvelope` | desktop-to-tablet |
  | 5 | `ReconnectPlan` | desktop-to-tablet |

  A receiver refuses an undefined kind or a kind for the other direction before parsing, and
  `CompanionProtocolJson.DeserializeRelayPayload` reads exactly the root the kind names.
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
  decryption and advances only after authentication. The desktop persists each session's receiver
  position with the session. Dropped frames are detected by the delivery stream, not repaired by
  the relay.

Failover never downgrades a paired session to group protocol v1, a shared group key, cleartext
HTTP, or an unauthenticated LAN channel. If an authenticated confidential route is unavailable, the
paired session fails closed and reports the effective transport failure.

The relay privacy inventory is intentionally small:

- for frames: protocol major/minor, opaque channel ID, session routing ID, cipher suite, key epoch,
  sender sequence, ciphertext length, issue/expiry time, receipt time, and network metadata
  inherent to HTTPS;
- for handshakes: the public material listed in the wire-roots table, including the sealed name's
  ciphertext length;
- never: workspace, map, floor, viewport, selection, search, result, mark, coordinate, capture
  context, progress, correction, profile context, preference, protected-item rule, favorite loadout,
  device display name, payload kind, traffic key, or reusable authorization material.

The relay cannot mint canonical acknowledgements. The authenticated desktop assigns server UTC,
delivery sequence, device identity, revisions, and command results after decryption.

## Versioning and negotiation

Versions are `major.minor`, with major 1–99 and minor 0–999. The current paired protocol is 2.0.
A compatibility window covers one major. Peers select the highest minor in the intersection of
their windows and send only the selected version. `ServerHello` distinguishes compatible,
client-upgrade, desktop-upgrade, and no-shared-major outcomes. A compatible hello carries only a
negotiated version inside the desktop window; an incompatible one carries only a recovery action.
Pairing and resume requests must name the connection's negotiated version.

A command envelope must use exactly the session's negotiated version. A version the reader cannot
read is `UnsupportedVersion`, matching the v2 rule that the receiver cannot read the change. A
readable version that was not negotiated for the authenticated session is a peer error,
`RejectedInvalidState` with code `version-not-negotiated`. Both leave canonical state untouched.
Reconnect plans are stamped with the session's negotiated version, and a reconnect request in any
other version receives `UnsupportedVersion`. There is no shared-version fallback across majors.

A minor may add an optional object field or a reviewed closed-union member. Readers ignore unknown
optional object fields within a negotiated major. An unknown enum member, command discriminator,
workspace-action discriminator, update discriminator, server-message discriminator, relay payload
kind, or reconnect disposition fails closed. Removing or renaming a field, adding a required field,
changing meaning/order/time/authentication semantics, changing a handshake or transport-binding
encoding, or weakening a bound or safety invariant requires a new major and an ADR.

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

Every revision, delivery sequence, and mark revision on the wire is an integer from 0 to 2⁵³−1, so
a JavaScript client reads it exactly.

## Canonical aggregates and revisions

One `AuthorityEpoch` identifies a continuous desktop authority lifetime. Desktop restart or
canonical-state replacement mints a new epoch. The state names its v2 `WorkspaceId` and desktop
instance, has a monotonic global revision for reconnect coverage, and holds five independently
revisioned aggregates:

| Aggregate | Canonical content |
| --- | --- |
| Device modes | Per-device Follow/Control Pending/Control/Independent state, one pending request, and at most one control lease. |
| Workspace | Workspace, map/floor, versioned viewport, selection/deep link/focus token, visible objectives/plans, search/results, layers/filters, and allowed dialogs. |
| Marks | Manual pings, waypoints, route points, and notes, each a Core `MapMarkState` with author, mark revision, last change ID, scope, coordinate space, projection version, height, color, and times. |
| Capture intent | One short-lived context whose scan intent, armed time, and expiry are a Core `CaptureIntentState`, with origin, session/correlation, progress, result reference, guidance, review, and corrections. |
| Profile preferences | Exactly one active profile context (or none), preference schema version, item pins/wishlist, protected-item rules, recommendation overrides, favorite loadouts, and explicit shared-personalization opt-ins. Device-local presentation/accessibility settings and ephemeral pings are excluded. |

Each aggregate cursor holds its revision and the ID of the change occupying it (null exactly at
revision zero). Each command names the positive aggregate revision it intends to create: if the
current revision is `n`, a new command must request `n+1`. An applied change advances its own
aggregate revision and the global revision exactly once; an unrelated aggregate's revision never
makes a command stale. Canonical state is valid only when the five aggregate revisions sum exactly
to the global revision.

**V2 attribution.** Every canonical update repeats the authority epoch and carries the v2 change
attribution: the Core `WorkspaceOrigin` (workspace, authenticated device, `PairedDevice` or
`DesktopApplication`, and client or desktop instance) and the `V2ContractVersion`. Maintenance
changes are attributed to the desktop device and instance. The update's change ID must be the ID
occupying its aggregate cursor. A marks update projects each mark it
wrote to `RevisionedState<MapMarkState>` on stream `paired/<authority epoch>/Marks/<mark ID>`
(`MarksCanonicalUpdate.ToRevisionedStates`), and a capture update projects its intent to
`RevisionedState<CaptureIntentState>` on stream `paired/<authority epoch>/CaptureIntent`
(`CaptureCanonicalUpdate.ToRevisionedState`), with the update's aggregate revision, change ID,
origin, contract version, and time. Scoping streams to the epoch and using the aggregate revision
keeps every stream's revisions increasing, including when a mark ID is deleted and re-created.
Device modes and the workspace projection are paired state, not v2 payloads. The paired JSON options
serialize the reused Core DTOs byte-for-byte as `V2ContractJson.Options` does.

**Delivery budget.** Committed canonical state always fits the wire. After computing a change, the
reducer serializes the widest possible acknowledgement envelope carrying the full new state
(`CanonicalDeliveryBudget`), with every revision and sequence at 2⁵³−1, through the lexical boundary.
A change whose state would come within 2 KiB of 64 KiB, or exceed a string, array, or depth bound, is
rejected before commit. Server-time maintenance never adds a mark, device, or capture; it only
removes entries or rewrites a status, mode, cursor, or timestamp in place, which that reserve
absorbs, so an acknowledgement, snapshot, or update can always be delivered after maintenance too.
To keep depth inside 16, a paired capture result carries at most three provenance levels; the full
#264 lineage stays with the referenced recognition result.

## Command acknowledgements

This section reconciles the paired acknowledgement with the acknowledgement rules of
`docs/V2_CONTRACT.md`. `AppliedRevision` is the aggregate revision the acknowledgement describes
and `AppliedChangeId` is the change occupying it, null exactly when that revision is zero. Only
`Applied` may name the acknowledged command as the applied change, so comparing `AppliedChangeId`
with `CommandId` tells the command that landed from every other outcome. `CommandAcknowledgement`
enforces the table on construction and when it is read:

| Disposition | V2 disposition | Revisions | `AppliedChangeId` | Canonical state |
| --- | --- | --- | --- | --- |
| `Applied` | `Applied` | applied = requested | this command | absent |
| `RejectedStale` | `RejectedStale` | applied > requested | another change | present |
| `RejectedConflict` | `RejectedConflict` | applied = requested > 0 | another change | present |
| `UnsupportedVersion` | `UnsupportedVersion` | applied = 0 | null | absent |
| `RejectedExpired` | — | applied = 0 | null | absent |
| `RejectedUnauthorized` | — | applied = 0 | null | absent |
| `RejectedInvalidState` | — | applied = 0 | null | absent |
| `RejectedCommandIdReuse` | — | applied = 0 | null | absent |
| `UnsupportedPreferenceSchema` | — | applied = 0 | null | absent |
| `RequiresPreview` | — | the aggregate cursor | another change, or null at zero | present |
| `RequiresSnapshot` | — | the aggregate cursor | another change, or null at zero | present |

When canonical state is present, its epoch, global revision, and the acknowledged aggregate's cursor
are exactly those the acknowledgement names. A rejection without canonical state describes no
revision, so it can never be read as naming the rejected command, or a reused identifier's original
change, as applied.

The first four rows use the v2 disposition names with the v2 revision and change-ID rules;
`CommandAcknowledgement.CoreDisposition` maps them, and tests construct the Core
`StateAcknowledgement` from every such reducer acknowledgement. The paired conflict is narrower than
the v2 row: a command that jumps past the next revision, including past revision zero where v2 has
no conflicting change, is `RequiresSnapshot` rather than a conflict. The remaining rows are narrower
paired rejections that leave state untouched. A client acknowledgement of a canonical update is
always a v2 `Applied` of a desktop change, because a tablet never rejects canonical state; a tablet
that cannot read an update reconnects instead of acknowledging it.

Rejection reasons, in reducer order:

1. The envelope's session or desktop flag disagrees with the authenticated context: `RejectedUnauthorized`.
2. The version cannot be read: `UnsupportedVersion`. A readable version other than the session's
   negotiated version: `RejectedInvalidState`.
3. A retained receipt exists for the command ID: the same device resending the same action
   fingerprint is an idempotent `Applied` duplicate with code `duplicate-command`; anything else is
   `RejectedCommandIdReuse`.
4. The ID is a version-8 UUID, reserved for desktop maintenance changes, or already occupies an
   aggregate cursor: `RejectedCommandIdReuse`.
5. The command has expired, was issued more than one minute in the desktop's future, or was issued
   at or before the receipt horizon: `RejectedExpired` or `RejectedInvalidState`.
6. The context lacks the capability: `RejectedUnauthorized`.
7. The envelope names another authority epoch: `RequiresSnapshot`.
8. An offline preview is not bound to the current epoch and aggregate revision: `RequiresPreview`.
9. Revision below, equal to, or beyond the next: `RejectedStale`, `RejectedConflict`, `RequiresSnapshot`.
10. The transition itself is invalid, unauthorized, or would exceed the delivery budget.

**Idempotency.** A command ID is immutable and global, not per device. The desktop keeps at most 256
`RecentCommandReceipt` entries with the command's action fingerprint, authenticated device,
aggregate, applied revision, issue time, and expiry. A receipt remains until expiry and while its
change occupies an aggregate cursor; cursor-pinned receipts are never the entries evicted to meet
the bound. A same-device resend of the same action is the duplicate while its receipt remains, even
when a re-preview refreshes revision, lifetime, or offline-preview metadata. The duplicate reports
the landed revision and current global revision without changing state. When the bound evicts an
unexpired receipt, the receipt horizon advances to its issue time; a conforming retransmission keeps
that immutable issue time and is rejected fail-closed with `idempotency-window-exceeded`. Relay-frame
sender sequences independently reject byte-for-byte network replay before decryption. A compromised
paired endpoint can sign a fresh permitted action under either a new ID or rewritten metadata, so
an unbounded authority-lifetime tombstone set would add denial-of-service cost without constraining
that endpoint's effective authority.

The fingerprint is `CanonicalCommandFingerprint`: the command serialized through the closed
polymorphic model without its delivery metadata (`commandId`, `requestedRevision`, `issuedUtc`,
`expiresUtc`, and `offlineQueuePreview`), then hashed as a tagged binary tree with object members
sorted by ordinal name, arrays in order, strings as unescaped UTF-8, and numbers as the serializer's
round-trip text. The discriminator and every action field, including payload revisions such as a
mark's expected revision, take part. It does not depend on JSON member order or escaping.
Fingerprints are desktop-local and never cross the wire. Receipts and the receipt horizon are not
part of the serialized snapshot; #277 persists both beside canonical state.

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
not JSON Patch, arbitrary property paths, script, operating-system control, or EFT control. A
requested lease is a whole number of milliseconds up to five minutes.

Disconnect and revocation are enforced three times. The adapter calls `ApplySessionTermination`
when a session closes, is revoked, expires, or is replaced, and `ApplyDeviceTermination` when a
device is revoked, expires, or is replaced. Independently, `ApplyMaintenance(state, now,
pairedDevices, sessions)` runs at least hourly and on every lifecycle change; it releases any lease
or pending request whose session or device is no longer live and removes mode entries of terminated
devices, so a missed disconnect event cannot leave a device in Control. Between maintenance passes,
the reducer treats an expired pending request or lease as absent: it never blocks a new request, an
expired lease never authorizes a control action, and the next request returns the expired holder to
Follow.

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
The deep link must match `tarkov-companion://<kind>/<segment>[/<segment>…]`, where the kind is a
lowercase letter followed by at most 31 lowercase letters, digits, or hyphens, and each of one to
four segments is 1–128 unreserved characters (`A–Z a–z 0–9 . _ ~ -`) other than `.` or `..`. The
desktop routes it inside the companion and never passes it to the operating-system shell, a browser,
or a file handler.

## Coordinates and marks

A mark's map, floor, plane coordinates, label, and expiry are exactly one Core `MapMarkState`; the
label is at most 80 characters, the Core cap. The paired mark adds its kind, scope, coordinate
space, projection version, optional height, and `#RRGGBB` color. In `World` space the plane X/Y and
the height are finite and bounded to ±1,000,000; in `Normalized` space X/Y are in `[0,1]` and there is
no height. Every mark names a map, optional floor, and projection version, and the map consumer
validates the named projection before drawing it. Desktop pixels are absent. `MapCoordinate`
remains the viewport center coordinate.

Every mark preserves a server-derived author device, per-mark revision, the ID of the change that
wrote that revision, type, scope, and creation/update UTC. Scope is `Private` (displayed only by the
author device and the desktop), `PairedDevice` (displayed on every paired device of this desktop),
or `Team` (eligible for downstream team publication). Every paired device belongs to the same user
and receives every mark in canonical state, so `Private` is a presentation rule, not access control;
only `Team` can ever leave the paired devices. A tablet may edit or delete its own marks; desktop
authority may repair them. Creating, editing, or deleting a team-scoped mark requires
`PublishTeamMarks` and downstream team opt-in. A mark revision that disagrees with the cached one,
or an edit of a missing mark, is `RequiresSnapshot`. The protocol does not publish a device as a
team member.

A ping lives at most 45 seconds from its creation, including after edits, and is never persistence
input. Waypoints, route points, and notes may persist. Server-time maintenance removes expired marks
as a revisioned change. Completion derived downstream from the user's own last-known position may
publish a final completion state to a team; no live position enters this paired-device contract.

## Contextual capture intent

A paired capture intent's scan intent, armed time, and expiry are exactly one Core
`CaptureIntentState`. The scan intents are the frozen #264 `ScanIntent` values `Auto`, `Loot`,
`Stash`, `Ammo`, `Keys`, `QuestItems`, `ExtractsAndMap`, and `HealthAndCharacter`
(`PairedScanIntents.Allowed`). Flea recognition remains a valid v2 Core result, but `Flea` is never a
paired-device capture intent in protocol 2.0, and a paired intent always has an expiry.

An authorized desktop or tablet can submit `RequestCaptureIntentCommand` while no unexpired,
unfinished intent exists. The desktop validates the device/session/capability, aggregate revision,
queue preview, context, and expiry, then creates `ContextualCaptureIntent` with the authenticated
initiating device and surface. It expires within two minutes of being armed and carries a Core
`CaptureSessionId`, correlation ID, map/floor/profile context, previous-result reference, and
bounded objective/plan/mark references.

Only the authenticated desktop reports progress and publishes recognition results; a paired device
cannot gain that authority through a capability grant. The desktop reports ordered progress for
Armed, Awaiting user capture, Settling, Decoding, Detecting
context, Detecting regions, Matching, Enriching profile, Recommending, Awaiting review, and terminal
Complete/Cancelled/Failed stages. Progress preserves sequence, capture artifact and ordinal where
applicable, UTC, percent, and detail. As in the v2 capture contract, a Cancelled or Failed stage that
names an artifact ends only that capture: the intent returns to Awaiting user capture, or keeps a
published result awaiting review, and that artifact can no longer produce a result. The same stage
without an artifact ends the whole intent. A guided follow-up capture reported after a result keeps
that result awaiting review. Results reference the typed #264 recognition result by result/artifact
ID and capture ordinal and retain completeness, freshness, detected context, completion UTC, and at
most three provenance levels. A reviewed or corrected result cannot be replaced; re-recognition
starts a new intent. Result completion cannot precede the armed request, its correlated progress, or
the provenance observation, and cannot follow the desktop's awaiting-review transition. Guidance is
a closed manual instruction such as take a user screenshot, open
the relevant panel, scroll for overlap, review ambiguity, or confirm the result. None performs the
instruction.

Review is Accepted or Needs correction and preserves reviewer device/time/note. Corrections are
append-only and ordered from one, with a closed correction kind, field ID, bounded corrected value,
reviewer device, UTC, and reason. They augment rather than replace the #264 evidence/correction
history. Only an accepted review makes the contextual capture Complete. Expiry, cancellation, or
failure after a result keeps that result as history.

## Delivery and backpressure

Every server envelope to a device consumes exactly one sequence of that device's single delivery
stream, in the order the desktop enqueues it, whatever the message. The stream belongs to one
authority epoch: the desktop persists the ledger's assigned sequences with canonical state for the
life of the epoch, and a new epoch starts a new ledger whose sequences restart. `DeliveryLedger` is
the executable model:

- Canonical updates enter the channel of their aggregate; acknowledgements, snapshots, and
  deprecation notices enter the control channel. Each channel of each device is bounded to 64
  pending deliveries.
- A full channel coalesces its pending deliveries into one snapshot-required marker at the next
  sequence. The transport sends a marker as a canonical snapshot of the state current when it is
  sent. Other channels of the device and every other device keep accepting deliveries, so a slow
  tablet cannot block unrelated state.
- `ClientDeliveryAcknowledgement` acknowledges the whole device stream through one sequence. It is
  accepted only from the authenticated session at its negotiated version and contains exactly one
  cursor for every aggregate whose revisions sum to the global revision. History is trimmed only
  when that complete vector exactly matches current canonical state and the sequence was assigned.
  An old valid acknowledgement is ignored; a stale, partial, divergent, wrong-session, or
  wrong-version acknowledgement leaves the ledger unchanged.

`CanonicalReplica` is the executable tablet reading of that stream and the reference #290 mirrors:

| Delivery | Replica result |
| --- | --- |
| Session or protocol version differs from authenticated negotiated context | Discarded; nothing changes. |
| Update attribution differs from the envelope's authenticated origin or canonical workspace/kind, its change is after server UTC, server UTC regresses, or its aggregate would violate canonical composition | Resync required; nothing is applied and malformed state never escapes the replica as an exception. |
| Any delivery from another authority epoch | Resync required; only a correlated reconnect plan can adopt the new lifetime. |
| Sequence at or below the last applied | Duplicate; nothing changes. |
| Same-epoch canonical snapshot at a newer sequence | Replaces the cache only when every aggregate cursor dominates the held state and equal cursors have equal content; rollback or divergence requires resync. |
| Any other delivery while awaiting resync | Discarded. |
| Sequence other than last + 1 | Resync required: a delivery-sequence gap is never applied as a delta. |
| Update whose global revision is at or below the cache and whose aggregate revision is older, or whose equal cursor identity and content exactly match | Already reflected (a snapshot contained it); the position advances. An equal-revision fork requires resync. |
| Update whose global revision is not exactly the next, or whose aggregate revision is not exactly the next | Resync required. |
| Next update | Replaces that aggregate and advances the global revision. |
| Acknowledgement carrying same-epoch canonical state | Applies only when it dominates the cache; stale state is ignored and divergent state requires resync. |
| Other acknowledgement or deprecation notice | Advances the position. |

A replica requiring resync sends `ReconnectRequest` on the live session.

## Reconnect and replay

A reconnect request sends a client-generated request ID, the negotiated version, session, cached
authority epoch (null after a reload with no cache), last global revision, last contiguous delivery
sequence, and exactly one acknowledgement per aggregate when a cache exists. The aggregate revisions
sum to the global revision. `ReconnectPlanner.Plan(canonical, request, ledger, device, session,
negotiatedVersion)` first binds the inner request to the authenticated session. It returns a plan
stamped with the same session, negotiated version, and request ID, plus the unchanged ledger:

- `UnsupportedVersion` when the request version cannot be read or is not the negotiated version;
- `FullSnapshot` after an epoch change or missing cache, an incomplete or impossible cursor vector,
  a claimed sequence the desktop never assigned, a coalesced marker or gap in the retained stream,
  more than 256 deliveries to replay, retained updates whose global and aggregate revisions do not
  prove a contiguous path to every current cursor, or a replay plan that would exceed 64 KiB;
- `UpToDate` only when the complete vector exactly matches current canonical state and the client
  already holds the last assigned sequence;
- `Replay` of the retained messages with their original sequence, authenticated origin, and server
  UTC otherwise.

`ResumeAfterDeliverySequence` is the device's last assigned sequence; the client adopts it after
applying the plan and live delivery continues from the next sequence. Sending a plan never
acknowledges delivery or trims history; only the client's later authenticated, exact-current
`ClientDeliveryAcknowledgement` does that. A snapshot is
authoritative and atomically replaces the tablet's cache before deltas resume.
`CanonicalReplica.ApplyReconnectPlan` accepts only the response to the named outstanding request on
the authenticated session and negotiated version, plus the server UTC authenticated by that
desktop-to-tablet frame. The response time advances the replica's rollback fence, and no replay item
may claim a later time. It adopts a snapshot from another authority epoch
only while that request still describes the replica. Within an epoch, it refuses a snapshot whose
global or aggregate cursors would roll back or diverge from state received since the request,
discards a plan whose resume position is behind the replica, skips replayed deliveries already
applied, requires a replay to start no later than the next expected sequence, and applies `UpToDate`
only at the replica's own position. Expired control or capture commands never replay because commands
are never replayed, only their canonical results.

## Offline actions

An offline tablet may cache the last canonical snapshot locally. Local Independent navigation,
search, filter, selection, control, capture progress, and profile preference edits have no offline
draft type and are never
queued. `OfflineActionQueue` holds at most 64 uniquely identified explicit drafts: Show on desktop,
mark upsert, mark delete, and capture-intent request. Each expires within fifteen minutes of
queueing and is pruned at expiry. The lifetime bounds how stale a draft can be; it does not know raid
boundaries, so the preview below, not the clock, is what keeps an old draft from applying to changed
state.

A draft is not a command. After reconnecting, the tablet shows each draft against the current
desktop state it just received, and the user previews, edits, or discards it. `PrepareSubmission`
then builds one command whose ID is the draft ID, whose issue time is the queue time, which requests
the next revision of the previewed state, and whose `OfflineQueuePreview` binds the approval to that
state's authority epoch and aggregate revision. Because the action fingerprint excludes the
revision and preview, resubmitting the same draft after a lost acknowledgement is the idempotent
duplicate of the change that landed, even after a re-preview. Any intervening change produces
`RequiresPreview`; the action is not rebased or applied automatically, and the next draft for the
same aggregate is previewed against the state after the previous one landed. A command type that is
not queue-eligible has no preview parameter, so a preview smuggled in JSON is ignored rather than
honored.

## Bounds and JSON validation

All transports call `CompanionProtocolJson` rather than default serializer options.

| Item | Bound |
| --- | --- |
| Plaintext root, including a full snapshot or reconnect plan | 64 KiB |
| Committed canonical state | the widest acknowledgement carrying it stays 2 KiB under 64 KiB |
| Relay frame plaintext | payload kind plus root: 3–65,538 bytes |
| Relay frame JSON | 96 KiB |
| Any UTF-8 string field | 1,024 bytes; identifiers use narrower limits where defined |
| Mark label | 80 characters |
| Selection deep link | 512 characters of the companion grammar |
| Wire integer (revision, sequence, mark revision) | 0 through 2⁵³−1 |
| Collection | 256 entries |
| JSON depth | 16; paired capture provenance is at most three levels |
| Paired devices | 32 |
| Marks | 256 |
| Recent idempotency receipts | 256 newest; a receipt whose change occupies a cursor is never evicted |
| Offline actions | 64, each for fifteen minutes |
| Delivery channel | 64 per device and channel |
| Replay | 256 deliveries |
| Timestamp | explicit zero UTC offset (`+00:00` or `Z`) when read, including inside reused Core DTOs; millisecond precision |
| Client clock skew accepted for issue times | one minute |
| Pairing code | ten Crockford base32 symbols; five attempts per source hash per five minutes; 160 observations, then fail closed |
| Pairing offer | five minutes normally, ten minutes maximum |
| Handshake challenge | pairing offer lifetime for pairing; two minutes for session resume |
| Session | twelve hours, and never past the device's expiry |
| Control lease | two minutes normally, five minutes maximum, whole milliseconds |
| Capture intent | two minutes |
| Profile preference items / protected rules / overrides | 256 each |
| Favorite loadouts / items per loadout | 64 / 64 |
| Shared-personalization entries | 64 |
| Preference quantity and sort order | 0–1,000,000; loadout quantity starts at 1 |
| Device absence expiry | two hours since the last recorded use unless explicitly revoked/replaced sooner |
| Key epoch | 1 through 2³²−1, strictly increasing per device |
| Sender sequence | 1 through 2³²−1 per session direction |
| Maintenance scan | at least hourly, with exact server-time expiry still enforced on use |

The lexical pass rejects oversize payloads and strings, invalid UTF-8, overlong arrays, excessive
depth, comments, trailing commas, case-insensitive duplicate keys, and non-JSON numeric forms. It
rejects these member names, compared case-insensitively: `$type`, `$id`, `$ref`, `typeName`,
`clrType`, `assemblyQualifiedName`, `password`, `passphrase`, `secret`, `apiKey`, `authorization`,
`cookie`, `sessionToken`, `shortCode`, `pairingCode`, `privateKey`, `sharedSecret`, `trafficKey`,
`accessToken`, `refreshToken`, `authorizationToken`, `bearerToken`, and `groupKey`. Canonical options
reject integer enum values, strings for numbers, missing constructor parameters, null required
references, and unknown discriminators. Constructors reject undefined enum values, empty IDs,
non-canonical base64url, key IDs that are not their key's thumbprint, public keys whose point is
not on P-256, COSE keys for another algorithm or curve, unsigned challenges, non-finite or
out-of-range coordinates, labels over the Core cap, deep links outside the grammar, integers above
2⁵³−1, illegal timestamps, invalid lifecycle combinations, inconsistent acknowledgements, and
mutable-list substitution by copying collections. Every such failure while reading a root surfaces
as `JsonException`, so a transport has exactly one rejection path.

Unknown optional object fields remain readable inside a negotiated major, but they do not bypass
the lexical security checks. Secrets and reusable authorization material are absent from every
wire root, schema, golden vector, log, URL, and diagnostic shape. The test-only private keys in the
crypto vectors are derived from public labels and protect nothing.

## Profile preference continuity

`ProfilePreferences` is a separate delivery channel and revision cursor. Its snapshot contains at
most one active `PreferenceProfileContext`, never a bag of every profile on the desktop. The context
key includes stable profile ID, generation, game mode, wipe, locale, and data-snapshot identity and
publication time. A tablet command repeats that complete key; any difference returns
`RequiresSnapshot` with the current canonical state. This prevents a command prepared before a
rapid profile, PvP/PvE, wipe, locale, or catalog switch from mutating the new profile.

The preference document is a closed schema, currently 1.1. Version 1.0 is readable and migrates to
1.1 by adding the explicit shared-personalization collection; canonical snapshots and persistence
always carry 1.1. A later minor may add an optional field only with an append-only migration in
`PreferenceSchemaPolicy`. A higher minor or another major returns `UnsupportedPreferenceSchema`
without a state change. Unknown optional protocol fields can be ignored safely because paired devices submit
only one closed field-level `ProfilePreferenceMutation`; they never replace the whole document.
Thus an older device can change a field it understands without erasing a newer protected rule,
override, loadout, or sharing decision. Only the authenticated desktop can activate or switch a
full profile document, after normalizing it to the current schema.

The closed mutations set or remove one item pin/wishlist entry, upsert or delete one protected-item
rule, set or delete one recommendation override, upsert or delete one favorite loadout, or set or
delete one shared-personalization opt-in. `ResetProfilePreferencesCommand` preserves the current
context and replaces its contents with schema defaults. `DeleteProfilePreferencesCommand` visibly
clears the active document. Both require the same context, schema window, next aggregate revision,
and `ManageProfilePreferences` capability as a mutation. Activation, mutation, reset, deletion, and
profile switch are ordinary canonical updates with authority epoch, global and aggregate revision,
change ID, UTC, and `WorkspaceOrigin`. The aggregate retains its last origin and UTC in snapshots;
normal delivery and reconnect acknowledgements include its cursor. Generic revision rules provide
the conflict outcome: stale, equal, and gapped writes cannot silently win.

The collections are deterministic and bounded: 256 item entries, protected rules, and overrides;
64 favorite loadouts with 64 items each; and 64 shared-personalization entries. Identifiers are
unique within each collection. Quantity and sort values are 0–1,000,000 (loadout quantities start
at one), while the enclosing 64 KiB delivery budget remains the tighter whole-document limit.

## Evidence

GitHub Actions runs these tests on every change; local runs are supplementary.

| Claim | Test |
| --- | --- |
| Every root has a golden vector that round-trips and validates strictly against the schema; every discriminator, enum, and shared Core bound in the schema matches the C# model | `GoldenAndHostileJsonTests` |
| Structured mutations of every golden vector fail only with `JsonException`; unknown optional fields from a newer minor stay readable; byte fuzz and hostile deep links fail closed | `GoldenAndHostileJsonTests` |
| Nonce commitment, handshake context, sealed name, commitment, code, transcripts, signature input, ECDH secrets, traffic keys, nonce, AAD, typed relay plaintext, and the relay frame match an independent implementation | `CryptographyVectorTests` |
| Pairing is commit-then-reveal and requires approval, the code, a desktop signature, and a WebAuthn proof; resume is single-use with increasing key epochs; relay substitution is detected; the rate limiter fails closed | `HandshakeTests` |
| Session roots are framed on every transport; the pairing code, QR payload, and source hash match the independent vectors | `TransportBindingTests` |
| Acknowledgements satisfy the disposition table and the Core `StateAcknowledgement` rules; hostile command sequences never escape the reducer; this document and `docs/V2_CONTRACT.md` do not drift | `AcknowledgementContractTests` |
| Duplicates across refreshed delivery metadata, mutated reuse, cross-device reuse, reserved IDs, bounded receipts, the receipt horizon, and irreversible replay rejection after receipt expiry | `IdempotencyTests` |
| Zero-offset timestamps inside Core DTOs; Core DTOs serialize as under `V2ContractJson.Options` | `GoldenAndHostileJsonTests` |
| Desktop-local changes, disconnect/revoke/expiry to Follow, expired requests and leases, maintenance enforcement and delivery budget, v2 attribution and Core projections, desktop-only capture evidence, result chronology, per-capture terminal stages, context construction | `DeviceModeLifecycleTests`, `CanonicalStateMachineTests` |
| Delivery sequencing, exact authenticated acknowledgements, isolation, gap detection, session/version/origin/time binding, replica resync, correlated late plans, a restarted authority epoch, and provable reconnect plans | `DeliveryAndReconnectTests` |
| The 64-action offline bound, expiry, preview binding, and eligibility | `OfflineActionQueueTests` |
| Negotiation and deprecation | `CompatibilityTests` |
| Profile context isolation, 1.0→1.1 migration, field-scoped forward compatibility, all closed mutations, reset/delete, bounds, attribution, replica, reconnect, and acknowledgement | `ProfilePreferencesTests` |

## Consumer handoff

Issue #277 owns authentication adapters, protected credential storage, session persistence,
desktop approval UI orchestration, and transport composition. It must:

- keep the desktop identity key DPAPI-protected behind `IDesktopIdentitySigner`, and implement
  `IDeviceKeyProofVerifier` with the pinned RP ID, origin, signature, and counter checks above;
- serve the transport binding exactly: the offer endpoint and redacted header, framed-only session
  traffic on the LAN gateway, relay channel registration, `ComputeSourceHash` with a per-start
  random key feeding `PairingRateLimiter`, and the relay's own per-source limiter for forwarded
  lookups;
- bind requests with the connection's negotiated version, release `RevealNonce` only after binding,
  and show the opened name, key fingerprint, and verification code in an approval prompt that cannot
  approve until the user confirms the codes match, for QR and typed-code pairing alike;
- create devices with `DeviceLifecycle.Pair`, persist `SessionResumeAttempt` by challenge ID, record
  sessions with `DeviceLifecycle.RecordSession` and frames with `DeviceLifecycle.RecordUse`, and
  persist each session's `RelayFrameReceiver` position;
- open frames with `RelayFrameReceiver` and `OpenRelayFrame`, read them with
  `DeserializeRelayPayload`, construct authenticated context only with `ForPairedSession`, call
  `ApplySessionTermination`, `ApplyDeviceTermination`, and `ApplyMaintenance` as described, and end a
  device's previous session as `Replaced` after resume;
- persist canonical state, its receipts, receipt horizon, irreversible consumed-command-ID set, and
  the delivery ledger atomically for the life of an authority epoch, mint a new epoch whenever that
  state is lost, enqueue every returned acknowledgement and update through `DeliveryLedger` with
  authenticated origin, resolve
  markers at send time, validate delivery acknowledgements with authenticated device, session, and
  negotiated version, and plan reconnects with that same session context. Keep the ledger returned by
  `ReconnectPlanner` until the client sends an accepted exact-current acknowledgement;
- load only the active profile's preferences into `ProfilePreferences`, normalize persisted 1.0
  documents through `PreferenceSchemaPolicy`, apply the closed commands through the reducer, and
  atomically persist the resulting profile-scoped document without copying it into another context;
- route selection deep links inside the companion and never pass them to the operating-system shell.

Issue #290 owns tablet presentation and local Independent state. It consumes the schema and golden
vectors; pins the desktop identity key from the QR payload or verification code and uses that pinned
key for every resume; checks the nonce reveal against the offer before showing the code and
`SessionEstablished` against the verified challenge; displays the code and mode/lease/security/
conflict/expiry state; sends session traffic only in frames; resends a command byte-for-byte unchanged
when it retries one; creates a fresh reconnect request ID and accepts only the plan that echoes its
session, negotiated version, and outstanding request ID; corrects its clock from `ServerUtc`; mirrors
`CanonicalReplica` exactly; uses `OfflineActionQueue` semantics for explicit drafts; and keeps
reusable authorization material out of browser storage. It cannot create a second JavaScript
state-machine interpretation: server acknowledgements, snapshots, and updates are authoritative.

The local gateway and hosted relay are transport adapters. They do not change authorization,
revision, conflict, expiry, projection, or capture meaning. Changes to a discriminator, bound,
state transition, handshake or transport-binding encoding, key schedule, relay-readable field, or
canonical aggregate require a protocol review and version/ADR decision; a change to a reused Core
DTO is also a v2 contract change.

This document and ADR define a protocol, not evidence that a future gateway or relay adapter is
securely implemented. As required by `docs/security/TBD_COMPONENTS.md`, #304 and downstream
composition work must add the implemented paired-device trust boundary, lost-device and
pairing-token abuse cases, failover/downgrade analysis, and tested controls to the living threat
model before release.
