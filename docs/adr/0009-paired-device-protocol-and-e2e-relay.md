# ADR 0009: Make the desktop canonical and encrypt paired state through the relay

Status: Accepted — 2026-09-14; amended 2026-09-15 after the independent protocol audit, re-audit,
and preference-continuity review

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
acknowledgement contract. An independent audit blocked the implementation on those gaps. Its
re-audit then found that a relay could grind a pairing request until the verification code
matched, that direct-LAN traffic had no binding to an authenticated session, that resume proofs
could be replayed, and that paired updates did not carry the v2 attribution and payload types.

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
per-source rate limit that fails closed when full. The human code is ten Crockford base32 symbols,
a redacted transport lookup, not an authenticator and not JSON. The desktop has a long-lived ECDSA
P-256 identity key. Resolving the code returns an offer with the identity key, a desktop ephemeral
key, and only a commitment to the desktop nonce. The tablet binds a WebAuthn ES256 credential, its
own ephemeral key and nonce, and a display name sealed to the desktop ephemeral key. The desktop
binds that first request, and only then reveals the nonce; the tablet checks it against the
commitment. Both screens show a six-digit code derived from the request and the revealed nonce, and
the user approves only on a match, in both flows; the QR payload additionally pins the identity key
for the tablet, but only the compared code authenticates the tablet's request to the desktop. Because the
request is fixed before the nonce is known, nobody can choose a request that produces a chosen
code. The desktop then signs a transcript that binds the purpose, version, attempt, commitment,
device, key, credential, identity key, both ephemeral keys, both nonces, assigned session, relay
channel, cipher suite, key epoch, and lifetimes. The tablet verifies that signature and proves the
same transcript hash with a user-verified WebAuthn assertion. A session resume repeats the signed
transcript and device proof for an already paired, live device, with fresh ephemeral keys, the
identity key pinned at pairing, and a key epoch above every epoch the device has used; each resume
challenge establishes at most one session. Every byte of these encodings is fixed in
`docs/PAIRED_DEVICE_PROTOCOL.md` and pinned by vectors computed by an independent implementation.

**Transport binding.** A session is authenticated by its traffic keys, never by a session ID a peer
presents. After the handshake, the direct LAN gateway and the hosted relay both carry only
authenticated frames on one WebSocket framing; TLS is an outer layer. The offer endpoint, pairing
code header and format, QR payload, rate-limit source hash, relay channel registration, pinned
WebAuthn RP ID and tablet origin, clock guidance, and the residual risk of a compromised tablet
application origin are normative in the same document.

**Relay.** Paired session content is end-to-end encrypted on every route. HKDF-SHA-256 derives
distinct directional AES-256-GCM keys from the ECDH secret with the transcript hash as salt. The
plaintext starts with an authenticated payload kind that names the root and is legal only in its
direction, so the relay cannot tell a command from an acknowledgement or update. The nonce is the
key epoch and a strictly increasing sender sequence. The additional data authenticates the
direction and all routing metadata. Ciphertext chunking is canonical, and a receiver rejects
replayed, reordered, expired, and foreign frames before decryption. The hosted relay sees only
version, opaque channel/session routing, cipher suite, key epoch, sender sequence, ciphertext length,
expiry/receipt time, unavoidable network metadata, and the public handshake material. Workspace,
selection, capture, coordinate, name, and authorization content is not relay-readable.

**Acknowledgements.** The paired acknowledgement keeps the v2 meaning of applied revision and
applied change ID, and only `Applied` names the acknowledged command as applied. `Applied`,
`RejectedStale`, `RejectedConflict`, and `UnsupportedVersion` obey the v2 revision and change-ID
rules and map to the Core dispositions; `UnsupportedVersion` means the version cannot be read, and a
readable but unnegotiated version is an invalid-state peer error. A command jumping past the next
revision, or computed in another authority epoch, is a separate `RequiresSnapshot` instead of a
conflict. Rejections that return canonical state describe the aggregate cursor; every other
rejection, identifier reuse included, describes applied revision zero with no applied change. A
command ID identifies one change for the whole authority lifetime: a retry is a duplicate when the
same device resends the same action fingerprint, which excludes the requested revision, lifetime,
and offline preview a re-previewed retry refreshes, and any other reuse is
`RejectedCommandIdReuse`. The reducer returns a typed rejection for every hostile command instead of
throwing, and it refuses to commit state that could not be delivered inside the wire bounds with a
fixed reserve that covers later server-time maintenance.

**V2 alignment.** `docs/V2_CONTRACT.md` names this protocol as the governed paired-device transport.
Every paired update carries the Core `WorkspaceOrigin` and `V2ContractVersion`; marks carry the Core
`MapMarkState` with its 80-character label cap, and capture intents carry the Core
`CaptureIntentState` without flea recognition. Both project to `RevisionedState<T>`. Contract tests
keep the two documents from drifting. The amendment was made in the paired-protocol PR under a
coordinator-approved ownership exception rather than as a separate prerequisite contract PR.

**Delivery.** Every envelope to a device consumes one sequence of a single device stream. Each
device/channel queue is bounded independently; overflow coalesces that channel to a snapshot marker
without blocking other channels or devices. A tablet never applies a delivery after a sequence gap,
from another epoch, or with a non-contiguous revision; it asks to reconnect. Replay requires the
retained stream to cover every sequence and global revision after the client's position without a
marker and within the replay and payload bounds; otherwise the desktop sends a snapshot. A delivery
stream belongs to one authority epoch, so a tablet adopts a snapshot from a new epoch whose stream
restarted, and within an epoch never applies a late reconnect plan behind its position. Offline
submission is limited to Show on desktop, mark mutation, and capture-intent request drafts, at most
64 for fifteen minutes each, and each submission is bound to the epoch and aggregate revision the
user previewed.

Every transport uses the same closed roots, JSON options, centralized bounds, schema, golden vectors,
reducer, delivery ledger, replica rules, and reconnect planner.

**Profile preferences.** Profile preferences are their own canonical aggregate and delivery
channel. A snapshot carries only the active full profile context and a normalized preference schema,
not every local profile. Paired devices submit closed field-level mutations rather than whole
documents; only the authenticated desktop activates or switches a full document. Reset and delete
are explicit commands. Context mismatch, stale revision, and unreadable preference schema have
typed outcomes before mutation. The aggregate includes item pins/wishlist, protected-item rules,
recommendation overrides, favorite loadouts, and explicit shared-personalization opt-ins, while
excluding device-local presentation/accessibility state and ephemeral marks. This keeps an older
tablet from erasing fields it does not understand and prevents state from crossing profile, mode,
wipe, locale, or catalog boundaries. Preference schema 1.0 migrates append-only to canonical 1.1;
future migrations extend the policy rather than reinterpret old bytes.

## Alternatives considered

**Let the relay read paired state.** This would simplify routing and server-side debugging, but the
relay has no canonical-state responsibility and does not need the data. It would expand the privacy
and breach boundary to searches, notes, profile context, capture context, and coordinates. Rejected.

**Use only the short pairing code.** This would be easy to type but would not bind later sessions to
a device key and would turn a low-entropy locator into a bearer credential. Rejected.

**Derive the code from the offer and request alone.** The first version did this, but the offer
revealed the desktop nonce before any request existed, so a relay holding back the tablet's request
could try about a million requests of its own in a second until the desktop's code matched the
tablet's. Committing to the nonce and revealing it only after binding removes the choice. Rejected
in favor of commit-then-reveal.

**Carry session roots in plaintext over direct TLS.** A LAN envelope names only a session ID, which
the relay can see, so a gateway would have to invent an authenticator or trust that ID. Framing
every route with the session's traffic keys needs no second mechanism. Rejected.

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

**Let tablets replace an entire preference document.** It makes a stale or older client capable of
dropping newer fields it never saw, even if the outer aggregate revision is current. Rejected in
favor of closed field-level mutations, explicit reset/delete, and desktop-only activation.

**Give the tablet an independent canonical replica.** Multi-primary reconciliation would add
conflict rules to every aggregate and make control ownership ambiguous. The product already has a
complete desktop and can keep offline tablet browsing local. Rejected.

## Consequences

Issue #277 must implement DPAPI-protected desktop identity and device material, the WebAuthn
verifier (signature, RP ID hash, origin, counter), atomic persistence of canonical state with its
idempotency receipts, authenticated context construction from live session records, lifecycle and
maintenance calls, single-use resume attempts with recorded key epochs, recorded device use, the
transport binding on both routes, delivery ledger handling, relay receiver checks, and direct/relay
transport adapters. The approval prompt must show the verification code. Issue #290 consumes authoritative
server state, pins the desktop identity key, mirrors the replica and offline-draft rules exactly,
and may implement local Independent browsing, but it cannot fork reducer semantics. The local
gateway remains usable when the hosted relay is unavailable after pairing/recovery material exists.

The relay cannot inspect payloads for product debugging or content moderation. Diagnostics must use
explicit endpoint-safe metadata and user-reviewed reports; encryption keys and plaintext never
enter relay logs. Metadata such as timing, channel reuse, ciphertext size, handshake public keys,
and network endpoints is still visible and must not be described as hidden.

A user who approves without comparing the verification code can be attacked by a malicious relay,
whether the tablet scanned the QR payload or typed the code: a relay that forwards the code lookup
can bind its own request first, and nothing but the compared code tells the desktop which request is
the user's tablet. The approval prompt therefore cannot approve until the user confirms the codes
match. With commit-then-reveal, a relay that substitutes a
request matches the tablet's six-digit code with probability one in a million per attempt, and each
failed attempt consumes the one-time code within the per-source rate limit. End-to-end encryption
does not protect against a compromised tablet application origin, which is therefore deployed apart
from the relay and treated as part of the trusted computing base.

Failover cannot silently reuse group protocol v1 credentials or accepted cleartext LAN HTTP. A
paired route that cannot preserve device authentication and confidentiality fails closed and makes
its effective transport failure visible.

P-256/HKDF/AES-GCM, ECDSA desktop identity signatures, WebAuthn, the documented byte encodings, and
the transport binding are compatibility commitments for protocol 2.0. Replacing the key schedule or any encoding, changing
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
