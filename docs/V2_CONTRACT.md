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
preferred to documentation asking callers not to use them, so the generic envelopes are closed:
`RecognitionResultEnvelope`, `HistoricalIntelligence`, `ModelledIntelligence`, and
`RevisionedState` accept only the exact payload types in the frozen `V2WirePayloads` allowlists.
A type elsewhere that implements a marker interface is still rejected at construction. Adding a
payload is a contract change.

## Evidence envelope

Every recognized field, grid cell, result, recommendation, historical aggregate, and modelled
estimate can retain:

- a source class and source identifier;
- the UTC time it was observed;
- data-through and generation UTC times where applicable;
- confidence, including the distinction between unscored and a score of zero;
- coverage or sample size;
- producer version and model version;
- the provenance of each input when the claim is computed from several;
- source-pixel bounds where pixels supplied the claim;
- ordered candidates and their own provenance; and
- append-only corrections that retain the original value and evidence.

`EvidenceSourceClass` is a closed vocabulary. `Unknown` means no source could be established;
`GameWrittenScreenshot`, `ExternalVisiblePixels`, `GameWrittenLog`, and `ScreenshotFilename`
describe game-visible evidence without implying privileged access. `HistoricalAggregate` and
`ModelledEstimate` must carry data-through time, generation time, coverage, scored confidence,
and model version. Their constructors reject an incomplete envelope.

`DerivedCalculation` marks a combined figure such as value per occupied square. It names every
input's provenance and a generation time, and no input may be newer than that time. Direct source
classes cannot name inputs. A claim computed from a `ModelledEstimate` anywhere in its input tree
must itself be a `ModelledEstimate`, so arithmetic cannot turn a prediction into an ordinary
number. Input trees are bounded to depth 8 and 256 entries.

Observed time is when this application acquired the evidence. Data-through time is the newest
input represented by an aggregate or model. Generated time is when that aggregate or estimate
was produced. Capture time, carried by a recognition header, is when the pixels were taken and
may precede observed time. They are different claims and must not be substituted for one
another. Contract timestamps are normalized to UTC at construction and serialized with an
explicit zero offset.

`Value` is the current value. Corrections are numbered from one, never predate the evidence or
the correction before them, and each starts from the value the previous one left; the current
value is the last correction's value and `RecognizedValue` is the original. Only scalar values
are corrected in place. A composite payload is corrected through its own evidenced fields.
Candidate and correction lists are copied at construction and exposed read-only.

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
carries no value, and a `Complete` value must carry one. Presence is the test, so numeric,
boolean, and enum payload fields use nullable types and an undetermined quantity serializes as
absent rather than as a read zero. Enums reserve zero: it is either undefined or named `Unknown`,
so a missing or defaulted field cannot read as a real member. Undetermined contexts, raid-clock
bases, extract availability, and limb states are absent values rather than members.

## Contextual capture

Every capture begins with a user or paired-device request and a `CaptureSessionId`. `ScanIntent`
is frozen as `Auto`, `Loot`, `Stash`, `Ammo`, `Keys`, `QuestItems`, `ExtractsAndMap`,
`HealthAndCharacter`, and `Flea`.

Progress is ordered by a session-local sequence through `Armed`, `AwaitingCapture`, `Settling`,
`Decoding`, `DetectingContext`, `DetectingRegions`, `Matching`, `EnrichingProfile`,
`Recommending`, `AwaitingReview`, and a terminal `Complete`, `Cancelled`, or `Failed` stage.
`Armed` and `AwaitingCapture` belong to the session and name no artifact. `Settling` through
`AwaitingReview` belong to one capture and name its artifact and capture ordinal, so a guided
multi-image session keeps each capture's progress separate. A terminal stage with an artifact
ends that capture; without one it ends the session.

A capture's stages only move forward, except that a decode retry may return from `Decoding` to
`Settling`. Nothing follows a capture's terminal stage, and nothing at all follows the session's.
An ordinal names exactly one artifact. Progress cannot predate the request or move backwards in
time. A session cannot complete while a capture is unfinished. The session status cannot claim
more than its history: a failed session is `Unavailable`, a cancelled one is not `Complete`, a
complete one is `Partial` or `Complete`, and an unfinished one is `Unknown` or `Partial`.

The source boundary returns a reference to a user-initiated visible capture; it exposes no
process handle, hook, packet source, overlay surface, or input/control operation.

Captured images are transient by default. Pixels and local filesystem paths are not recognition
or workspace protocol fields. Debug Capture remains the sole explicit opt-in retention path and
must obey the privacy rules in `docs/SAFETY.md`.

## Typed recognition

Recognition is a closed set of typed result envelopes rather than one nullable bag. V2 defines
item, grid, loot, stash, ammo, key, quest-item, flea-page, extract/map, health/character, and
unresolved-context results. Every envelope carries the contract version, result and capture IDs,
artifact ID, capture time, requested intent, the detected context as an evidenced value, status,
and complete evidence metadata. Recognized fields and grid cells use the same evidence container.
Null evidence containers, value objects, and list entries are rejected, lists are copied at
construction, positional properties cannot be reassigned through `with`, and identifier structs
deserialize through their validating constructors. Quantities, prices, dimensions, and counts are
bounded in the value, every candidate, and every correction.

An item carries its displayed footprint (width and height in cells after rotation), a rotation
flag, stack quantity, found-in-raid state, and visible condition (durability, uses, charges, or
resource, or explicitly not applicable), so fit, swap, and value-per-square calculations need no
local DTO. A grid cell is anchored at the top-left cell of that footprint and spans its width and
height; known footprints stay inside a known grid and do not overlap. A container item that was
opened names its nested container path.

A stash result is a set of capture regions, each with its own identity, capture ordinal, artifact,
container path, and evidenced origin in container coordinates. Overlap is the intersection of
placed footprints, and a region with an undetermined origin is not stitched. Each captured
container has one coverage entry of observed and total cells, so closed or unscrolled space is
reported rather than invented. A nested container path extends its parent's path, its parent is
covered, and a cell in a region of that parent opened it.

A flea page keeps each row's own bounds separately from the item icon, the item's condition, and
every raw OCR line.

An `Auto` request reports the detected context; it does not coerce an uncertain frame into a
requested shape. A typed result requires a complete detected context of its own kind. A frame
that cannot be placed returns `UnresolvedContextRecognitionResult`, whose detected context is
absent and whose candidates, bounds, confidence, provenance, and corrections describe the
ambiguity. User or paired-device review appends a correction with its origin, time, and sequence
instead of replacing OCR evidence.

Extract rows distinguish `Exfil` from `Transit` (a way to another map, with a destination map ID
and no catalog match) and read availability as `Active`, `Conditional`, `Pending`, or `Closed`.
The slot label is kept as read.

The extract/map result permanently carries each raw OCR line before matching or filtering. The raid
clock is an evidenced reading whose basis is `ObservedOnExtractScreen` or `CountedFromRaidStart`
and whose as-of UTC instant is when the clock showed that value. An unread clock is an absent
reading, not a basis. An observed clock comes from screenshot or visible-pixel evidence and is as
of the header's capture time, which may precede its provenance observed time; the clock ages from
the capture time. A counted clock comes from game-written log, user-entered, or derived evidence.
The raw line `Find an extraction point 0:28:10` and its observed-clock provenance are regression
fixtures. A counted map duration must never be presented as an observed remaining time.

Health recognition represents an absent or illegible display as unknown or unavailable; it
does not turn missing pixels into full health or a destroyed limb.

## Recommendations

Recommendations are reviewable advice. A result contains a contract version, ruleset version,
optional capture session, and an evidenced decision. A decision has an action, a summary, at
least one categorized reason ordered by priority, an evidenced opportunity cost, and the facts
that would change the answer. Actions such as take, keep, swap, leave, sell, use, or organize
describe what a user may choose; they are not commands and cannot perform game actions.

Reason categories are explicit override, safety, current found-in-raid quest, current quest,
future quest, hideout, craft or barter, specialist utility, pin or wishlist, scarcity or
obtainability, economics, and evidence quality. The ruleset version decides precedence between
them; the category keeps personal need distinct from market value. Loot economics may include
value per occupied square with `DerivedCalculation` provenance, but unknown quantity, dimensions,
price, or profile context produces partial or unknown advice and an absent opportunity cost
rather than an invented number.

## Historical and modelled intelligence

Historical aggregates and modelled estimates carry only allowlisted payloads: zone traffic
intensity, route corridor pressure, and encounter likelihood, each scoped to a map and raid phase
with a value between 0 and 1. Each result names its inputs and carries source,
observed/data-through/generated UTC, coverage or sample size, scored confidence, producer
version, and model version. Modelled estimates and their candidates serialize only with
`ModelledEstimate` provenance; historical summaries and their candidates serialize only with
`HistoricalAggregate` provenance.

Inputs are typed. Static map data is public or curated data; public structured data is public
data; historical aggregates are historical aggregates; curated knowledge is curated data; and
private local feedback is user-entered or game-written log evidence. No other source class can
back an input, so a screenshot, a paired-device action, or another model's estimate cannot enter a
model. No input may be newer than the output's data-through time.

Presentation must say “historical,” “modelled,” or “predicted,” show freshness and confidence,
and never use language implying a detected person or current location. Model inputs may include
static spawns, map topology, points of interest, extracts, elapsed raid phase, and bounded past
observations. They may not include EFT packets, process memory, or live player observations.

## Workspace, device, revision, and acknowledgement

Every cross-device change identifies a workspace, device, instance, stream, change ID, contract
version, origin, UTC time, and stream-local revision, and carries an allowlisted state payload:
an armed capture intent or the user's own map mark. Revisions are monotonic per stream, not a
single global counter; zero means nothing applied and a change starts at revision one.

Receivers acknowledge the exact change with its requested revision, the receiver's resulting
revision, the change's contract version, and the receiver's contract version:

| Disposition | Rule |
| --- | --- |
| `Applied` | The receiver can read the version and the revisions are equal. |
| `RejectedStale` | The receiver can read the version and already holds that revision or later. |
| `RejectedConflict` | The receiver can read the version and holds an earlier, divergent revision. |
| `UnsupportedVersion` | The receiver cannot read the change's version and left its stream alone. |

Origin metadata is audit evidence, not authentication. Pairing, credential storage, session
expiry, and transport authorization must be supplied by the tablet security workstream before
remote changes are accepted. An unauthenticated origin is never trusted merely because it names
a known device.

## Evolution and ownership

The current contract version is 2.0; majors are bounded to 1-99 and minors to 0-999. A reader
accepts a message of its own major version up to its own minor version. Peers negotiate the lower
minor of a shared major and send only that, so a newer minor never reaches an older reader, and
anything else is `UnsupportedVersion`. Readers ignore unknown optional object fields within the
same major version; an unknown enum or payload type is rejected, not silently mapped. A minor
version may add optional fields or allowlisted payload types. Removing or renaming fields,
changing a field's meaning, adding a required field, changing ordering or timestamp semantics, or
weakening a safety invariant requires a major version and an ADR.

Every transport serializes v2 contracts with `V2ContractJson.Options`. Default serializer options
fail open: they accept integer enums, turn a missing constructor argument into a default ID or
enum, and pass null to non-nullable references. The canonical options reject all three, and
constructors enforce the remaining invariants.

The v2 contract owner owns this document, its ADR, `Domain/Evidence`, and `Abstractions/V2`.
Feature worktrees consume these contracts and place orchestration in Application, external I/O
in Infrastructure, Windows APIs in Platform.Windows, and presentation in App. Contract changes
arrive as focused prerequisite PRs reviewed by the integration owner; feature branches do not
quietly fork DTOs. V1-to-v2 adapters live downstream and must preserve provenance rather than
manufacture missing evidence.

## Enforcement

CI runs constructor/serialization tests, public-surface architecture tests, and the safety audit.
Architecture tests hold the closed allowlists, zero-reserved enums, get-only properties, absent-
capable evidenced payloads, and validating struct constructors. Positive fixtures prove ordinary
window discovery, visible user capture, public data access, and historical modelling remain
expressible. Negative fixtures prove known memory, injection/hook, packet-capture, generated
keyboard/mouse/controller input, and overlay APIs or dependencies fail the audit. Each fixture
line must be caught and each audit pattern must catch a fixture line of its own.

Patterns target the prohibited capability rather than adjacent API names. Ordinary process lookup,
physical hotkey observation, companion-window placement, an always-on-top companion, and an owned
companion dialog stay allowed. The overlay tripwire is a click-through window, a topmost layered
window, or a window re-owned by the game's window. The source audit is a ratchet, not a proof;
semantic architecture tests separately assert that v2 protocols have no game-control or live-enemy
vocabulary.
