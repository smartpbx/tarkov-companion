# ADR 0009: Make the desktop canonical and encrypt paired state through the relay

Status: Accepted — 2026-09-14

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

## Decision

The paired protocol begins at 2.0 as a separate closed contract. The desktop is its sole canonical
authority. It holds an authority epoch, monotonic global revision, independent aggregate revisions,
the applied change ID at each revision, and a bounded idempotency window. Clients send typed
intentions for one exact next aggregate revision. The desktop returns an applied, duplicate,
pending-approval, stale, conflict, expired, unauthorized, invalid-state, preview-required, or
unsupported-version result. Stale and conflicting input never wins by arrival time.

Follow, ControlPending, Control, and Independent form one deterministic state machine. Control
requires a desktop-approved device/session-bound lease and the desktop can preempt it. Independent
browsing is local and has no replay command. Show on desktop is one explicit action and does not
leave Independent mode. Offline submission is limited to Show on desktop, mark mutation, and
capture-intent request, and requires a current desktop preview bound to the authority epoch and
aggregate revision.

Pairing uses a five-minute single-use offer with a ten-minute hard maximum and a per-source rate
limit. The human code is a redacted transport lookup, not an authenticator and not JSON. The first
resolved request binds a WebAuthn ES256 public credential. A local desktop approval produces a
challenge and pairing completes only after that credential proves the challenge. Sessions bind
the device ID and key ID and expose explicit close, revoke, expire, and replace states. Desktop
private material uses DPAPI downstream; the browser relies on its platform authenticator and
memory-only session keys rather than a reusable bearer value in browser storage.

Relayed paired content is end-to-end encrypted. Pairing establishes ephemeral P-256 ECDH material;
HKDF-SHA-256 derives distinct directional AES-256-GCM keys from a transcript containing the
negotiated version, pairing attempt, device-key thumbprint, both ephemeral public keys and nonces,
session, and relay channel. Key epoch and sender sequence prevent nonce reuse and are authenticated
with the routing metadata. The hosted relay sees only version, opaque channel/session routing,
cipher suite, key epoch, sender sequence, ciphertext length, expiry/receipt time, and unavoidable
network metadata. Workspace, selection, capture, coordinate, name, and authorization content is
not relay-readable.

Every transport uses the same closed commands, server messages, JSON options, centralized bounds,
schema, golden vectors, reducer, delivery ledger, and reconnect planner. A per-device/per-aggregate
delivery channel is bounded independently; overflow requests a snapshot for that channel rather
than blocking other aggregates or devices. Replay requires a matching epoch and provably contiguous
global revisions after the acknowledged delivery sequence; otherwise the desktop sends a snapshot.

## Alternatives considered

**Let the relay read paired state.** This would simplify routing and server-side debugging, but the
relay has no canonical-state responsibility and does not need the data. It would expand the privacy
and breach boundary to searches, notes, profile context, capture context, and coordinates. Rejected.

**Use only the short pairing code.** This would be easy to type but would not bind later sessions to
a device key and would turn a low-entropy locator into a bearer credential. Rejected.

**Use last-write-wins or JSON Patch.** This would make concurrent behavior depend on network timing
and let old/new clients disagree about arbitrary mutation paths. It also creates an extensible
control channel that the safety boundary cannot close by construction. Rejected.

**Give the tablet an independent canonical replica.** Multi-primary reconciliation would add
conflict rules to every aggregate and make control ownership ambiguous. The product already has a
complete desktop and can keep offline tablet browsing local. Rejected.

## Consequences

Issue #277 must implement WebAuthn proof verification, DPAPI-protected desktop material, atomic
canonical persistence, authenticated context construction, maintenance scheduling, and direct/relay
transport adapters. Issue #290 consumes authoritative server state and may implement local
Independent browsing, but it cannot fork reducer semantics. The local gateway remains usable when
the hosted relay is unavailable after pairing/recovery material exists.

The relay cannot inspect payloads for product debugging or content moderation. Diagnostics must use
explicit endpoint-safe metadata and user-reviewed reports; encryption keys and plaintext never
enter relay logs. Metadata such as timing, channel reuse, ciphertext size, and network endpoints is
still visible and must not be described as hidden.

Failover cannot silently reuse group protocol v1 credentials or accepted cleartext LAN HTTP. A
paired route that cannot preserve device authentication and confidentiality fails closed and makes
its effective transport failure visible.

P-256/HKDF/AES-GCM and WebAuthn are compatibility commitments for protocol 2.0. Replacing the key
schedule, changing relay-readable metadata, weakening device proof, or making a new operation
queueable is a security-relevant protocol change requiring an ADR and appropriate version change.

The contract does not itself implement storage, WebAuthn, DPAPI, HTTPS, or cryptography. It makes
the inputs, outputs, transitions, and relay privacy boundary those implementations must satisfy.
The implementing gateway/relay issues must extend the living threat model in `docs/security/` with
the actual paired-device trust boundary and executable abuse-case evidence; this ADR is not that
implementation review.
GitHub Actions remains the integration gate after the protocol and downstream projects are
registered in the solution.
