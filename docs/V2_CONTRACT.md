# V2 contract

This document is the normative contract for v2 feature work. It freezes vocabulary and trust
boundaries before implementations diverge; it is a target contract, not a claim that every v2
feature is already implemented. Existing v1 contracts remain supported until an integration
owner explicitly retires them.

When descriptions disagree, `AGENTS.md` and `docs/SAFETY.md` govern safety, this document governs
v2 evidence and protocol semantics, and an accepted ADR records why a decision exists. Feature
documents may narrow these rules but may not weaken them.

## Fixed boundary

Three anti-cheat fixtures are immutable:

1. Never read or write Escape from Tarkov process memory.
2. Never generate game-directed mouse, keyboard, or controller input.
3. Never draw an in-game overlay.

The current design also excludes injection and game/renderer hooks, inspection or decoding of
EFT network packets, flea or inventory automation, and live enemy detection, tracking, ESP, or
radar. These exclusions apply equally to desktop, tablet, relay, capture, recognition,
recommendation, and intelligence features.

The application may observe externally visible pixels after a user request, read files the game
wrote, accept local user state, and use public or curated structured data. Historical traffic
and modelled route-pressure guidance are allowed. They are summaries or estimates, never claims
about where another player is now.

No v2 protocol operation controls EFT. No source discriminator represents memory, game input,
packets, an overlay, or a live enemy sensor. A type that cannot express those capabilities is
preferred to documentation asking callers not to use them.

## Evidence envelope

Every recognized field, grid cell, result, recommendation, historical aggregate, and modelled
estimate can retain:

- a source class and source identifier;
- the UTC time it was observed;
- data-through and generation UTC times where applicable;
- confidence, including the distinction between unscored and a score of zero;
- coverage or sample size;
- producer version and model version;
- source-pixel bounds where pixels supplied the claim;
- ordered candidates and their own provenance; and
- append-only corrections that retain the original value and evidence.

`EvidenceSourceClass` is a closed vocabulary. `Unknown` means no source could be established;
`GameWrittenScreenshot`, `ExternalVisiblePixels`, `GameWrittenLog`, and `ScreenshotFilename`
describe game-visible evidence without implying privileged access. `HistoricalAggregate` and
`ModelledEstimate` must carry data-through time, generation time, coverage, scored confidence,
and model version. Their constructors reject an incomplete envelope.

Observed time is when this application acquired the evidence. Data-through time is the newest
input represented by an aggregate or model. Generated time is when that aggregate or estimate
was produced. They are different claims and must not be substituted for one another. Contract
timestamps are normalized to UTC at construction and serialized with an explicit zero offset.

## Result state

Completeness and freshness are separate axes:

| Completeness | Meaning |
| --- | --- |
| `Unknown` | No determination has been made. |
| `Unavailable` | A required provider or source could not produce a result. |
| `Partial` | Some bounded evidence was read, but the result is incomplete. |
| `Complete` | The recognizer completed the requested scope. |

| Freshness | Meaning |
| --- | --- |
| `Unknown` | No freshness judgment is available. |
| `Current` | The result is within the consumer's documented freshness policy. |
| `Stale` | The result is older than that policy permits. |

A result may be both `Partial` and `Stale`. Unknown, unavailable, and stale must never be collapsed
to an empty collection, false, zero, or complete result. An `Unknown` or `Unavailable` value
therefore carries no value. Presence is the test, so numeric, boolean, and enum payload fields use
nullable types and an undetermined quantity serializes as absent rather than as a read zero.

## Contextual capture

Every capture begins with a user or paired-device request and a `CaptureSessionId`. `ScanIntent`
is frozen as `Auto`, `Loot`, `Stash`, `Ammo`, `Keys`, `QuestItems`, `ExtractsAndMap`,
`HealthAndCharacter`, and `Flea`.

Progress is ordered by a session-local sequence through `Armed`, `AwaitingCapture`, `Settling`,
`Decoding`, `DetectingContext`, `DetectingRegions`, `Matching`, `EnrichingProfile`,
`Recommending`, `AwaitingReview`, and a terminal `Complete`, `Cancelled`, or `Failed` stage.
The source boundary returns a reference to a user-initiated visible capture; it exposes no
process handle, hook, packet source, overlay surface, or input/control operation.

Captured images are transient by default. Pixels and local filesystem paths are not recognition
or workspace protocol fields. Debug Capture remains the sole explicit opt-in retention path and
must obey the privacy rules in `docs/SAFETY.md`.

## Typed recognition

Recognition is a closed set of typed result envelopes rather than one nullable bag. V2 defines
item, grid, loot, stash, ammo, key, quest-item, flea-listing, extract/map, and health/character
results. Every envelope carries the contract version, result and capture IDs, requested intent,
detected context, status, and complete evidence metadata. Recognized fields and grid cells use the
same evidence container. Null evidence containers, value objects, and lists are rejected,
lists are copied at construction, and identifier structs deserialize through their validating
constructors.

An item carries its displayed footprint (width and height in cells after rotation), a rotation
flag, stack quantity, found-in-raid state, and visible condition (durability, uses, or resource),
so fit, swap, and value-per-square calculations need no local DTO. A grid cell's address is the
top-left cell of that footprint. A stash result is a set of capture regions, each with its
own identity, capture order, container path, artifact, and evidenced origin in container
coordinates; overlap is the intersection of placed footprints, and a region with an undetermined
origin is not stitched. Each captured container has one coverage entry of observed and total
cells, so closed or unscrolled space is reported rather than invented. A flea page keeps each
row's own bounds separately from the item icon, the item's condition, and every raw OCR line.

An `Auto` request reports the detected context; it does not coerce an uncertain frame into a
requested shape. Ambiguity preserves ordered candidates. User or paired-device review appends a
correction with its origin, time, and sequence instead of replacing OCR evidence.

The extract/map result permanently carries each raw OCR line before matching or filtering. The raid
clock is a typed field whose basis distinguishes `ObservedOnExtractScreen`, `CountedFromRaidStart`,
and `Unknown`, and whose as-of UTC instant is when the clock showed that value. For an observed
clock that is the screenshot capture time, which may precede the provenance observed time; the
clock ages from the capture time. An `Unknown` basis carries neither a remaining time nor an as-of
time. The raw line `Find an extraction point 0:28:10` and its observed-clock provenance are
regression fixtures. A counted map duration must never be presented as an observed remaining time.

Health recognition represents an absent or illegible display as unknown or unavailable; it
does not turn missing pixels into full health or a destroyed limb.

## Recommendations

Recommendations are reviewable advice. A result contains a ruleset version, ordered evidenced
reasons, a result status, and provenance. Actions such as take, keep, swap, leave, sell, use, or
organize describe what a user may choose; they are not commands and cannot perform game actions.

Personal quest, future-quest, hideout, wishlist, event, and explicit override needs remain
distinct from market value. Loot economics may include value per occupied square, but unknown
quantity, dimensions, price, or profile context produces partial/unknown advice rather than an
invented number.

## Historical and modelled intelligence

Historical aggregates and modelled estimates accept only allowed public, curated, local, or
game-written evidence. Each result names its inputs and carries source, observed/data-through/
generated UTC, coverage or sample size, scored confidence, producer version, and model version.
Modelled estimates serialize only with `ModelledEstimate` provenance; historical summaries
serialize only with `HistoricalAggregate` provenance.

Presentation must say “historical,” “modelled,” or “predicted,” show freshness and confidence,
and never use language implying a detected person or current location. Model inputs may include
static spawns, map topology, points of interest, extracts, elapsed raid phase, and bounded past
observations. They may not include EFT packets, process memory, or live player observations.

## Workspace, device, revision, and acknowledgement

Every cross-device change identifies a workspace, device, instance, stream, change ID, contract
version, origin, UTC time, and stream-local revision. Revisions are monotonic per stream, not a
single global counter. Receivers acknowledge the exact change and report `Applied`,
`RejectedStale`, `RejectedConflict`, or `UnsupportedVersion` with both requested and applied
revisions.

Origin metadata is audit evidence, not authentication. Pairing, credential storage, session
expiry, and transport authorization must be supplied by the tablet security workstream before
remote changes are accepted. An unauthenticated origin is never trusted merely because it names
a known device.

## Evolution and ownership

The current contract version is 2.0. Readers ignore unknown optional object fields within the
same major version. Senders use only capabilities negotiated with the receiver; an unknown enum
or union discriminator is rejected as `UnsupportedVersion`, not silently mapped. A minor version
may add optional fields or negotiated result types. Removing or renaming fields, changing a
field's meaning, adding a required field, changing ordering or timestamp semantics, or weakening
a safety invariant requires a major version and an ADR.

The v2 contract owner owns this document, its ADR, `Domain/Evidence`, and `Abstractions/V2`.
Feature worktrees consume these contracts and place orchestration in Application, external I/O
in Infrastructure, Windows APIs in Platform.Windows, and presentation in App. Contract changes
arrive as focused prerequisite PRs reviewed by the integration owner; feature branches do not
quietly fork DTOs. V1-to-v2 adapters live downstream and must preserve provenance rather than
manufacture missing evidence.

## Enforcement

CI runs constructor/serialization tests, public-surface architecture tests, and the safety audit.
Positive fixtures prove ordinary window discovery, visible user capture, public data access, and
historical modelling remain expressible. Negative fixtures prove known memory, injection/hook,
packet-capture, generated keyboard/mouse/controller input, and overlay APIs or dependencies fail
the audit, one fixture line at a time. Patterns target the prohibited capability rather than
adjacent API names: ordinary process lookup, physical hotkey observation, and companion-window
placement stay allowed. The source audit is a ratchet, not a proof; semantic architecture tests
separately assert that v2 protocols have no game-control or live-enemy vocabulary.