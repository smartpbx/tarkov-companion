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

Presentation uses progressive disclosure without discarding evidence. Routine views retain the
compact `Historical`, `Modelled`, or `Predicted` identity and put freshness or confidence inline
when decision-material. Full source and time lineage, coverage, confidence/calibration, and model
version remain available in on-demand `Why`/details and version-matched Setup/Admin `Data &
Privacy`; active consent/sharing/security state, destructive effects, failures, and
decision-changing uncertainty stay visible.

Generic envelopes are closed rather than documented: each accepts only exact payload types from
a frozen allowlist, so an enemy position or a control command cannot ride a recognition,
intelligence, or workspace-state envelope even if some type implements its marker interface.
Enums reserve zero for undefined or `Unknown`, positional properties cannot be reassigned, and
one canonical serializer configuration rejects integer enums, missing constructor arguments, and
nulls, because the default configuration turns each of those into a plausible value.

Every bound that protects a consumer is part of the frozen shape rather than a reader's habit.
Intelligence lineage is reconciled: the value, its candidates, and the typed input list must name
the same inputs, and no disallowed source may sit anywhere in an input's tree. An opportunity cost
names its price and footprint inputs in an explicit lineage and must be computed from them. Grids
live in a finite cell space checked before any expansion, an observed raid clock is under one
hour, capture ordinals are contiguous from zero in a session and ascending with gaps in a stash
result, and an acknowledgement names the change occupying the applied revision so a duplicate
delivery is distinguishable from a same-revision conflict. They are settled before 2.0 ships,
so they are part of 2.0 rather than a later major version.

Corrections remain append-only for scalar claims and for a closed set of Core-owned immutable
leaf records whose structural equality survives serialization: item condition, raid clock,
character region, and the three allowlisted intelligence payloads. Other composites are corrected
through their evidenced fields. An internal eligibility marker plus a sealed-type and assembly
check prevents an external or arbitrary reference type from opting into whole-value replacement.
Raid-clock source, basis, bound, provenance, and capture-time rules apply to every value,
candidate, correction, and result-level extract/map candidate at the public envelope and
serialization boundary. A shared linear footprint enumeration covers the current item, item
candidates, and every determined width/height value, candidate, and correction; both grid-relative
and absolute stash-container checks use it. The absolute check derives the furthest claimed origin
and the maximum geometry, cell, and span extents in linear work rather than multiplying origin and
grid claims. A placed stash cell is checked in absolute container space even when its region
dimensions were not read, using remaining room so origin-plus-offset arithmetic cannot overflow.
The public recognition envelope also binds every exact allowlisted payload type to its one complete
detected context, while the unresolved payload requires an absent current context even without a
named result wrapper. A conflict acknowledgement must name another change at a positive applied
revision because revision zero represents no applied change, and a transit carries no canonical
extract-ID value, candidate, or correction because it has no extract-catalog match.

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
rather than adjacent API names. Hidden and ignore-matched source belongs to the scan universe;
generated `bin` and `obj` output does not. The line scan prefers no-follow `rg` and intentionally
falls back to no-follow `grep -r` when `rg` is absent; a missing `rg` alone is therefore not an
error. A shared `lstat` traversal makes either line scanner fail on every file or directory symlink
under an owned root, and the Perl overlay traversal applies the same rule before its file tests.
File-link and directory-link self-tests exercise every available line scanner and Perl, so a
tracked source link cannot vanish from one scanner's universe. The selected scanner and the
required `git` and `perl` tools fail closed on absence or execution errors, and the overlay
capabilities that span statements are matched by statement rather than by line or identifier
naming. Pattern matching remains a narrow early warning and architecture tests enforce protocol
shape; neither is treated as a substitute for review. Transport authentication and feature
composition remain outside this ADR.
