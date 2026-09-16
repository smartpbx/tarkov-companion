# ADR 0018: Assemble guided full-stash scans as bounded evidence

Status: Accepted — 2026-09-16

## Context

A stash is taller than one screenshot and may contain bags, rigs, ammunition boxes, magazine
contents, key tools, document cases, and other nested grids. Counting each screenshot separately
double-counts overlap. Treating an unopened container as empty or extrapolating its contents makes
the count look exact while erasing what the player did not show. Items may also move while a
session is in progress, and repeated identical items make some apparent alignments ambiguous.

The capture-session checkpoint in ADR 0014 owns transient pixels and review. The v2 recognition
contract owns the grid and stash evidence shapes. Full-stash work therefore needs a downstream,
pixel-free assembly boundary rather than a second capture or recognition protocol.

## Decision

Accept one bounded ordered batch of reviewed, pixel-free stash frames. Each frame retains its
session, capture ordinal, artifact, correlation id, capture time, decode revision, container path,
frozen capture context, grid reconstruction, and visible-pixel provenance. A SHA-256 content
identity is admitted only as process-local deduplication state and never enters a result, export,
or database row.

Place a confirmed container start at zero. Otherwise use an explicit reviewed origin, or align a
frame only when at least two resolved occupied anchors produce one strictly best, conflict-free
offset. A tied offset remains unplaced. Input that arrives out of ordinal order is sorted for the
contract but also produces review guidance; failed capture ordinals remain gaps. Placed overlap is
counted once by absolute container cell. A different item or value at the same absolute anchor is
excluded as a movement conflict rather than choosing the newest reading.

Coverage is the union of placed captured cells against a separately evidenced container total.
Unknown geometry, origin, or total produces a lower bound or absent fraction, never full coverage.
Closed nested containers produce a guided sub-scan issue. A nested capture is retained only when a
covered parent region contains the cell that opened that exact child path. No closed-container
contents, ammunition/key identity, quantity, fee, age, scarcity, or obtainability is invented.

The result root is a `DerivedCalculation` naming the visible captures it combines. Persistence may
accept that root only when every internal node is another derived calculation and every leaf is
`GameWrittenScreenshot` or `ExternalVisiblePixels`. Mixed unknown, user, log, public, historical,
or modelled lineage is rejected. Direct visible-capture roots remain supported for existing
single-capture snapshots.

Snapshot comparison tracks `Observed`, `Inferred`, and `Unresolved` independently of confidence,
with `Absent` used only on one side of an addition/removal. Movement is claimed only for a unique
one-to-one occurrence signature; repeated indistinguishable items remain additions/removals.

Organization output is a manual checklist of Keep, Sell, Use soon, Organize, or Review groups.
It consumes the versioned v2 recommendation and separate fee, net, value-per-square, age,
scarcity, and obtainability facts. Ammo and key entries have a typed specialist status; without a
complete profile-aware result from #308 they stay in Review. The checklist can describe grouping,
consolidation, staging, and a sell queue, but it exposes no operation that can click, drag, type,
or mutate EFT.

Use the existing `observed_inventory_snapshots` and `observed_inventory_nodes` transaction
boundary. Saving a current snapshot retires the prior current record. Export reads the typed
pixel-free root. Explicit deletion promotes the newest surviving record in the same profile
scope, and retention deletes only non-current history older than a caller-supplied UTC cutoff;
there is no implicit retention default.

Keep the durable snapshot id, recognition snapshot id, and catalog/economics data snapshot id as
distinct identities. The existing `data_snapshot_id` column stores the last of those and export
includes it. No schema migration is needed because the column already exists. For compatibility,
the general inventory adapter still maps an omitted data snapshot id to the recognition snapshot
id for pre-existing callers; the guided stash workflow never omits it.

## Consequences

The assembly algorithm is deterministic, bounded, cancellation-aware, and fixture-testable
without Windows or captured pixels. Partial sessions remain useful as positive observations while
unknown and conflicting cells stay out of exact totals. More screenshots can therefore improve
coverage without silently increasing holdings through overlap.

Repeated identical items can require an explicit origin or another screenshot with distinctive
overlap. Closed containers require separate guided captures. Those pauses are deliberate: they
preserve evidence instead of manufacturing certainty.

Capture-session composition and a desktop review route remain owned by the runtime/UI integration
work. Until composed, these services are an end-to-end application/persistence slice but do not
claim a shipped recognition experience. GitHub Actions remains the required execution evidence.

The immutable anti-cheat boundary is unchanged: no EFT process memory, injection or hooks, packet
inspection, generated gameplay input, automation, live enemy/player tracking, radar/ESP, or
in-game overlay.
