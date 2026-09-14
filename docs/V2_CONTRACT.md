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
number. Input trees are bounded to depth 8 and 256 entries. Provenance equality is structural
over the whole input tree, in order, so the same lineage read back from JSON or named twice in one
message compares equal and can be reconciled against another copy of itself.

Observed time is when this application acquired the evidence. Data-through time is the newest
input represented by an aggregate or model. Generated time is when that aggregate or estimate
was produced. Capture time, carried by a recognition header, is when the pixels were taken and
may precede observed time. They are different claims and must not be substituted for one
another. Contract timestamps are normalized to UTC at construction and serialized with an
explicit zero offset.

`Value` is the current value. Corrections are numbered from one, never predate the evidence or
the correction before them, and each starts from the value the previous one left; the current
value is the last correction's value and `RecognizedValue` is the original. Scalars and the
closed set of Core-owned immutable leaf claims (`ItemConditionReading`, `RaidClockReading`,
`CharacterRegionReading`, `ZoneTrafficIntensity`, `RouteCorridorPressure`, and
`EncounterLikelihood`) are corrected in place with structural equality. Other composites are
corrected through their own evidenced fields. External or unlisted reference types cannot opt
into whole-value correction. Candidate and correction lists are copied at construction and
exposed read-only.

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
An ordinal names exactly one artifact. Capture ordinals number the session's queue from zero with
no gaps, and each capture first appears in queue order, so a lone ordinal 99 cannot read as a
normal history while the captures before it vanished. Progress cannot predate the request or move
backwards in time. A session cannot complete while a capture is unfinished. The session status
cannot claim more than its history: a failed session is `Unavailable`, a cancelled one is not
`Complete`, a complete one is `Partial` or `Complete`, and an unfinished one is `Unknown` or
`Partial`.

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
height; footprints stay inside the grid and do not overlap. A container item that was opened
names its nested container path.

Grids live in one finite cell space of 256 rows by 64 columns, whether or not a grid's own size
was read, and cells are at most 1024 pixels on a side. Rows, columns, spans, anchors, and cell
pixels are bounded in the value, every candidate, and every correction. Every anchor is placed
inside the grid (or that bounded space when the size was unread) before any footprint is expanded,
even when the item or its size is absent. The current item, every item candidate, and every
determined width or height claim in their values, candidates, and correction histories must fit on
its own. The same enumeration governs absolute container placement, so neither an item candidate
nor a span candidate can escape the 256-by-64 space. Fit is compared as remaining room, so no sum
can overflow, and a grid naming more footprints than it has cells is refused before any walk.
Expansion is therefore bounded by the cell space, not by the payload.

A stash result is a set of capture regions, each with its own identity, capture ordinal, artifact,
container path, and evidenced origin in container coordinates. Regions keep their session capture
ordinals, strictly ascending, and a failed or omitted capture leaves a gap rather than renumbering
the evidence. A placed region stays inside the container cell space. Overlap is the intersection of
placed footprints, and a region with an undetermined origin is not stitched. Each captured
container has one coverage entry of observed and total cells, at most 16,384 each, so closed or
unscrolled space is reported rather than invented. A nested container path extends its parent's
path, its parent is covered, and a cell in a region of that parent opened it.

Placement validation is linear in the evidence claims. It derives the furthest possible origin on
each axis once, then the maximum asserted geometry, cell, and footprint extent on that axis once;
it does not rescan every cell and span for every origin candidate. Extents are compared with the
remaining bounded container space before addition, so neither large candidate collections nor
coordinate arithmetic can amplify or overflow validation.

A flea page keeps each row's own bounds separately from the item icon, the item's condition, and
every raw OCR line.

An `Auto` request reports the detected context; it does not coerce an uncertain frame into a
requested shape. Each allowlisted payload type maps to exactly one `RecognizedContext`, and the
public `RecognitionResultEnvelope<T>` requires that complete context during direct construction
and deserialization; the named result wrappers are conveniences, not the coherence boundary. A
frame that cannot be placed carries `UnresolvedContextRecognition`, whose current detected context
must be absent while its candidates, bounds, confidence, provenance, and corrections describe the
ambiguity. User or paired-device review appends a correction with its origin, time, and sequence
instead of replacing OCR evidence.

Extract rows distinguish `Exfil` from `Transit` (a way to another map, with a destination map ID
and no canonical-ID value, candidates, or corrections because there is no extract-catalog match)
and read availability as `Active`, `Conditional`, `Pending`, or `Closed`.
The slot label is kept as read.

The extract/map result permanently carries each raw OCR line before matching or filtering. The raid
clock is an evidenced reading whose basis is `ObservedOnExtractScreen` or `CountedFromRaidStart`
and whose as-of UTC instant is when the clock showed that value. An unread clock is an absent
reading, not a basis. An observed clock comes from screenshot or visible-pixel evidence and is as
of the header's capture time, which may precede its provenance observed time; the clock ages from
the capture time. An observed clock is under one hour: no raid runs longer, and the game's
`??:??:??` marker for an undecided time OCRs as `22:22:22`, so the contract rejects that reading
(and `1:00:00` or longer) rather than relying on the reader to. A counted clock comes from
game-written log, user-entered, or derived evidence and is arithmetic rather than a pixel reading,
so it is not capped.
The source, basis, bound, and as-of rules apply to the recognized clock, every clock candidate,
and every correction. Result-level extract/map candidates are checked the same way, and every
observed reading in any of those paths is as of the result header's capture time. This is enforced
by the public `RecognitionResultEnvelope<ExtractMapRecognition>` constructor and deserialization
boundary, not only by an optional typed-result wrapper. The raw line `Find an extraction point
0:28:10` and its observed-clock provenance are regression fixtures. A counted map duration must
never be presented as an observed remaining time.

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

An opportunity cost is a computation, never a reading. A decision carries a required, nullable
`opportunityCostLineage` naming the two inputs the figure is computed from, `price` and
`footprint`, each a full provenance. It is null only when the cost carries no figure anywhere:
any value, candidate, or correction requires it. The value and every candidate then carry
`DerivedCalculation` or `ModelledEstimate` provenance whose input tree, depth first, holds the
price exactly once and then the footprint exactly once; other inputs such as quantity may sit
beside them. A bare catalog price or screenshot reading cannot carry a cost, and swapped, missing,
duplicated, identical, `Unknown`, null, or additional JSON roles are rejected.

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
model. The rule holds at every depth: an input whose own tree contains any source class other
than public, curated, historical-aggregate, user-entered, or game-written-log evidence is refused,
so a screenshot cannot hide beneath an allowed aggregate. The value serializes its own provenance
inputs beside the typed input list, and the value and every candidate must name exactly the listed
inputs, in order, so an allowlisted list cannot be advertised beside a different lineage. Each
input's evidence ID and provenance appear once, and the list holds at most 256 inputs. No input
may be newer than the output's data-through time.

Routine presentation keeps a compact category identity — “Historical,” “Modelled,” or
“Predicted” — and never uses language implying a detected person or current location. Freshness
and confidence appear inline whenever either could materially change a decision. Full source,
observed/data-through/generated timestamps, coverage, confidence and calibration, and model
version remain available on demand in a `Why` or details surface and in the version-matched
Setup/Admin `Data & Privacy` explanation; progressive disclosure never removes them from stored
evidence or relaxes validation. Active consent and sharing state, security state, destructive
effects, failures, and decision-changing uncertainty remain visible rather than being hidden in
details. Model inputs may include static spawns, map topology, points of interest, extracts,
elapsed raid phase, and bounded past observations. They may not include EFT packets, process
memory, or live player observations.

## Workspace, device, revision, and acknowledgement

Every cross-device change identifies a workspace, device, instance, stream, change ID, contract
version, origin, UTC time, and stream-local revision, and carries an allowlisted state payload:
an armed capture intent or the user's own map mark. Revisions are monotonic per stream, not a
single global counter; zero means nothing applied and a change starts at revision one.

Receivers acknowledge the exact change with its requested revision, the receiver's resulting
revision, the ID of the change occupying that revision (`appliedChangeId`), the change's contract
version, and the receiver's contract version. `appliedChangeId` is required but nullable: it is
null exactly when the applied revision is zero. Desktop and a paired tablet can both compute a
change against the same prior revision, so two different changes can request the same revision;
only the applied change ID tells a redelivery of the change that landed from a divergent change
that lost. A change creates exactly one revision, its requested one, so only `Applied` may name the
acknowledged change as the applied change.

| Disposition | Rule |
| --- | --- |
| `Applied` | Readable version, equal revisions, and the applied change is this change (a first apply or an idempotent duplicate delivery). |
| `RejectedStale` | Readable version, the receiver holds a strictly later revision, and it is another change. |
| `RejectedConflict` | Readable version and another change occupies the requested revision or an earlier positive, divergent revision; revision zero has no change to conflict with. |
| `UnsupportedVersion` | The receiver cannot read the change's version, left its stream alone, and does not name this change as applied. |

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
window, or a window re-owned by the game's window. The last two are matched by statement rather
than by line: the flag pair across ordinary multi-line formatting, and a `SetWindowLong(Ptr)`
`HWNDPARENT` call in the same or an adjacent statement as a quoted literal of the game's window or
process name, however the handle variable is named and whatever nested calls its arguments
contain. Build output under `bin` and `obj` is not scanned. Hidden and ignore-matched source is
scanned: it does not become safety-exempt because of a filename or local ignore rule. Every file
and directory under an owned scan root is inspected with `lstat` before file or directory tests;
any symbolic link fails the audit instead of being followed or silently omitted. The same
preflight governs both no-follow `rg` and recursive no-follow `grep -r`, and the Perl scanner
applies the same rule during its own traversal, so their source universes cannot disagree around a
link. Missing `rg` alone is not a failure because `grep` is the intentional fallback. Self-tests
require all available line scanners and Perl to reject both a compiled-C# file symlink and a
directory symlink. An error from the selected scanner, no available line scanner, or an unavailable
or failing required `git` or `perl` tool fails the audit instead of reading as no match. The source
audit is a ratchet, not a proof; semantic architecture tests separately assert that v2 protocols
have no game-control or live-enemy vocabulary.
