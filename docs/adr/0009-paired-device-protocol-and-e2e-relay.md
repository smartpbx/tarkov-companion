# ADR 0009: Make the desktop canonical and encrypt paired state through the relay

Status: Accepted — 2026-09-14; amended 2026-09-15 after the independent protocol audit

## Context

V2 lets the same user operate the standalone companion from a tablet. Follow, Control,
Independent browsing, one-shot Show on desktop, marks, and contextual capture all touch overlapping
state. If desktop and tablet each resolve concurrency, a reconnect can silently overwrite a newer
map, replay a stale capture intent into another raid, or let two control claimants both believe
they won. If JavaScript and C# interpret a loose JSON patch differently, that divergence becomes a
second authority.

The existing group relay protocol is for opt-in squad sharing and remains version 1. A paired
tablet is not a squad member and needs stronger identity than a group key. Pairing also cannot be
authenticated by a short human code: the code is observable, guessable under enough attempts, and
cannot identify the device that later reconnects.

Paired state includes searches, objectives, profile context, manual notes, map selections,
coordinates, and recognition context. TLS protects it in transit but would leave a hosted relay
able to read it. The relay does not need plaintext to route one user's devices.

The first accepted version of this decision named the right primitives but left an implementer
guessing at the parts that decide interoperability and security: which bytes are signed and in
what order, how the tablet knows it is talking to the real desktop rather than the relay, how a
reloaded browser gets a new session, what a duplicate command ID with a different payload means,
how a missing delivery is noticed, and how the paired acknowledgement relates to the v2
acknowledgement contract. An independent audit blocked the implementation on those gaps.

## Decision

The paired protocol begins at 2.0 as a separate closed contract. The desktop is its sole canonical
authority. It holds an authority epoch, monotonic global revision, independent aggregate revisions,
the applied change ID at each revision, and a bounded idempotency window. Clients send typed
intentions for one exact next aggregate revision computed in a named authority epoch. Stale,
conflicting, and gap-jumping input never wins by arrival time.

Follow, ControlPending, Control, and Independent form one deterministic state machine. Control
requires a desktop-approved device/session-bound lease that no device can approve for itself, and
the desktop can preempt it. A lease or request whose session or device stops being live returns
its device to Follow, both on the explicit lifecycle event and on the next maintenance pass, so a
missed disconnect cannot leave a device in control. Desktop-local workspace changes are ordinary
revisioned commands from the desktop context. Independent browsing is local and has no replay
command. Show on desktop is one explicit action, does not leave Independent mode, and cannot open
or dismiss a sensitive dialog without the administrative capability.

**Handshake.** Pairing uses a five-minute single-use offer with a ten-minute hard maximum and a
per-source rate limit. The human code is a redacted transport lookup, not an authenticator and not
JSON. The desktop has a long-lived ECDSA P-256 identity key. Resolving the code returns an offer
that commits the identity key, a desktop ephemeral key, and a desktop nonce before the tablet
reveals anything. The tablet binds a WebAuthn ES256 credential, its own ephemeral key and nonce, and
a display name sealed to the desktop ephemeral key. Both screens show a six-digit code derived from
all of that, and the user approves only on a match; the QR code additionally pins the identity key.
The desktop then signs a transcript that binds the purpose, version, attempt, commitment, device,
key, credential, identity key, both ephemeral keys, both nonces, assigned session, relay channel,
cipher suite, key epoch, and lifetimes. The tablet verifies that signature and proves the same
transcript hash with a user-verified WebAuthn assertion. A session resume repeats the signed
transcript and device proof for an already paired, live device, with fresh ephemeral keys. Every
byte of these encodings is fixed in `docs/PAIRED_DEVICE_PROTOCOL.md` and pinned by vectors computed
by an independent implementation.

**Relay.** Relayed paired content is end-to-end encrypted. HKDF-SHA-256 derives distinct
directional AES-256-GCM keys from the ECDH secret with the transcript hash as salt. The nonce is the
key epoch and a strictly increasing sender sequence. The additional data authenticates the
direction and all routing metadata. Ciphertext chunking is canonical, and a receiver rejects
replayed, reordered, expired, and foreign frames before decryption. The hosted relay sees only
version, opaque channel/session routing, cipher suite, key epoch, sender sequence, ciphertext length,
expiry/receipt time, unavoidable network metadata, and the public handshake material. Workspace,
selection, capture, coordinate, name, and authorization content is not relay-readable.

**Acknowledgements.** The paired acknowledgement keeps the v2 meaning of applied revision and
applied change ID. `Applied`, `RejectedStale`, `RejectedConflict`, and `UnsupportedVersion` obey the
v2 revision and change-ID rules and map to the Core dispositions; a command jumping past the next
revision, or computed in another authority epoch, is a separate `RequiresSnapshot` instead of a
conflict. The remaining paired rejections leave state untouched and never name the rejected command
as the applied change. A command ID identifies one change for the whole authority lifetime: a retry
is a duplicate only when the same device resends a command with the same canonical fingerprint, and
any other reuse is `RejectedCommandIdReuse`. The reducer returns a typed rejection for every hostile
command instead of throwing, and it refuses to commit state that could not be delivered inside the
wire bounds.

**Delivery.** Every envelope to a device consumes one sequence of a single device stream. Each
device/channel queue is bounded independently; overflow coalesces that channel to a snapshot marker
without blocking other channels or devices. A tablet never applies a delivery after a sequence gap,
from another epoch, or with a non-contiguous revision; it asks to reconnect. Replay requires the
retained stream to cover every sequence and global revision after the client's position without a
marker and within the replay and payload bounds; otherwise the desktop sends a snapshot. Offline
submission is limited to Show on desktop, mark mutation, and capture-intent request drafts, at most
64 for fifteen minutes each, and each submission is bound to the epoch and aggregate revision the
user previewed.

Every transport uses the same closed roots, JSON options, centralized bounds, schema, golden vectors,
reducer, delivery ledger, replica rules, and reconnect planner.

## Alternatives considered

**Let the relay read paired state.** This would simplify routing and server-side debugging, but the
relay has no canonical-state responsibility and does not need the data. It would expand the privacy
and breach boundary to searches, notes, profile context, capture context, and coordinates. Rejected.

**Use only the short pairing code.** This would be easy to type but would not bind later sessions to
a device key and would turn a low-entropy locator into a bearer credential. Rejected.

**Authenticate only the tablet.** WebAuthn proves the tablet to the desktop, but without a desktop
signature a relay could answer a tablet's pairing or resume as the desktop, learn its commands, and
feed it false state. A password-authenticated key exchange over the short code would need a
protocol-specific primitive browsers do not provide. A signed transcript plus a compared code uses
only WebCrypto and WebAuthn. Chosen.

**Send the device name in clear during pairing.** The name is what the approval prompt shows, but a
relay that routes pairing would learn it. Sealing it to the offer's ephemeral key costs one HKDF
and one AES-GCM operation and keeps the privacy boundary stated above. Chosen.

**Idempotency by command ID alone.** Treating any retained ID as a duplicate would acknowledge a
different payload as the change that landed, and per-device IDs would let two devices each be told
that their change occupies one revision. Rejected in favor of global IDs with canonical fingerprints.

**Use last-write-wins or JSON Patch.** This would make concurrent behavior depend on network timing
and let old/new clients disagree about arbitrary mutation paths. It also creates an extensible
control channel that the safety boundary cannot close by construction. Rejected.

**Give the tablet an independent canonical replica.** Multi-primary reconciliation would add
conflict rules to every aggregate and make control ownership ambiguous. The product already has a
complete desktop and can keep offline tablet browsing local. Rejected.

## Consequences

Issue #277 must implement DPAPI-protected desktop identity and device material, the WebAuthn
verifier (signature, RP ID hash, origin, counter), atomic persistence of canonical state with its
idempotency receipts, authenticated context construction from live session records, lifecycle and
maintenance calls, delivery ledger handling, relay receiver checks, and direct/relay transport
adapters. The approval prompt must show the verification code. Issue #290 consumes authoritative
server state, pins the desktop identity key, mirrors the replica and offline-draft rules exactly,
and may implement local Independent browsing, but it cannot fork reducer semantics. The local
gateway remains usable when the hosted relay is unavailable after pairing/recovery material exists.

The relay cannot inspect payloads for product debugging or content moderation. Diagnostics must use
explicit endpoint-safe metadata and user-reviewed reports; encryption keys and plaintext never
enter relay logs. Metadata such as timing, channel reuse, ciphertext size, handshake public keys,
and network endpoints is still visible and must not be described as hidden.

A user who pairs by typing the short code and approves without comparing the verification code can
be attacked by a malicious relay; the approval prompt must make the comparison explicit, and a
QR-scanned pairing avoids the dependency. Six digits give a one-in-a-million chance per attempt,
within the five-attempt rate limit.

Failover cannot silently reuse group protocol v1 credentials or accepted cleartext LAN HTTP. A
paired route that cannot preserve device authentication and confidentiality fails closed and makes
its effective transport failure visible.

P-256/HKDF/AES-GCM, ECDSA desktop identity signatures, WebAuthn, and the documented byte encodings
are compatibility commitments for protocol 2.0. Replacing the key schedule or any encoding, changing
relay-readable metadata, weakening device or desktop proof, changing acknowledgement dispositions,
or making a new operation queueable is a security-relevant protocol change requiring an ADR and an
appropriate version change.

The contract does not itself implement storage, WebAuthn, DPAPI, HTTPS, or transport. It provides
the reference encodings and the inputs, outputs, transitions, and relay privacy boundary those
implementations must satisfy. The implementing gateway/relay issues must extend the living threat
model in `docs/security/` with the actual paired-device trust boundary and executable abuse-case
evidence; this ADR is not that implementation review.
GitHub Actions remains the integration gate after the protocol and downstream projects are
registered in the solution.
