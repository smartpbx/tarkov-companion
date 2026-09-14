# ADR 0008: Freeze one evidence and safety contract for v2

Status: Accepted — 2026-09-14

## Context

V1 grew capture, OCR, recommendations, raid state, strategy, and group synchronization as
separate features. Their contracts consequently use several meanings of unknown confidence,
several result shapes, and nullable fields whose absence may mean unavailable, partial, stale,
or simply not applicable. Changing those public v1 records would break positional construction,
persisted enum names, and existing consumers.

V2 adds contextual scans, whole-stash recognition, paired tablet control of companion state,
and historical/modelled intelligence. Those workstreams need one provenance and revision model
before their implementations split into independent worktrees. The model must also make the
anti-cheat boundary enforceable rather than relying only on prose.

## Decision

V1 contracts remain unchanged. V2 contracts live in
`TarkovCompanion.Core.Abstractions.V2`; evidence primitives live in
`TarkovCompanion.Core.Domain.Evidence`. Core contains no platform implementation.

Every v2 recognized or generated claim uses a common evidenced-value shape containing status,
bounds, candidates, confidence, provenance, producer/model version, and append-only correction
history. Completeness and freshness are orthogonal, and an unknown or unavailable value carries no
value rather than a default that reads as zero or false. Recognition uses closed typed result
envelopes. Capture is user initiated and reports ordered stage progress. Cross-device state uses
stream-local monotonic revisions and explicit acknowledgements.

The evidence source vocabulary contains no privileged or live-enemy source. Historical and
modelled constructors require observed, data-through, and generation UTC timestamps, coverage,
scored confidence, and model version. A modelled result accepts only modelled-estimate
provenance, so deserialization cannot relabel it as a live observation. A figure computed from
several inputs names each input's provenance, and one computed from a model estimate stays a
model estimate.

Generic envelopes are closed rather than documented: each accepts only exact payload types from
a frozen allowlist, so an enemy position or a control command cannot ride a recognition,
intelligence, or workspace-state envelope even if some type implements its marker interface.
Enums reserve zero for undefined or `Unknown`, positional properties cannot be reassigned, and
one canonical serializer configuration rejects integer enums, missing constructor arguments, and
nulls, because the default configuration turns each of those into a plausible value.

Three anti-cheat fixtures do not change: no game process memory, generated game-directed mouse,
keyboard, or controller input, or in-game overlay. V2 also retains the current exclusions on
injection/hooks, EFT packet inspection, automation, and live enemy detection/tracking/ESP/radar.
Historical and modelled traffic guidance is allowed only under the evidence rules above.

## Consequences

Feature work gains a shared vocabulary and can evolve independently behind negotiated minor
versions. Adding a payload type is a reviewed contract change rather than a local generic
argument. Adapters must be explicit because missing v1 metadata cannot be invented. The new
types add some nesting, but that cost is the mechanism that prevents field and result evidence
from being discarded.

Safety auditing gains both allowed and prohibited fixtures and targets prohibited capabilities
rather than adjacent API names. Pattern matching remains a narrow early warning and architecture
tests enforce protocol shape; neither is treated as a substitute for review. Transport
authentication and feature composition remain outside this ADR.