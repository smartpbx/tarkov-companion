# ADR 0016: Bind Loot Scan plans to reviewed capture evidence

## Status

Accepted for incremental V2 delivery.

## Context

A loot screenshot can contain several visible items and only one visible carried-capacity budget.
Recognition can be partial, a user can redecode the retained frame, price and profile advice can
age independently, and the same cell can hold a different item after another capture. Matching a
recommendation or a drop policy by grid coordinate alone could therefore apply old advice to new
pixels. Planning every candidate independently could also tell two items to occupy the same free
cells.

The recommendation contract deliberately distinguishes market value from opportunity cost. In
particular, an economics-dominant recommendation has no opportunity cost because selling is the
dominant alternative. Loot planning cannot reuse that field as the incoming item's price.

## Decision

Core owns immutable Loot Scan evidence bindings, decisions, placements, swap items, economics,
and bounded planner limits. Application owns the capture-context request, result envelope, and
deterministic planning service.

- Every candidate recommendation and carried-item policy is bound to capture session, artifact,
  decode revision, content hash, grid anchor, and canonical item identity.
- Callers provide compact recommendation facts, not a precomputed recommendation result. The
  request carries one shared profile scope, data snapshot, observed inventory, and raid context;
  each candidate's profile and data-snapshot identity must match that shared context. The planning
  service invokes the versioned recommendation engine with the bound item, capture session,
  `Loot` use case, and scan evaluation time.
- The request retains the capture correlation id and context frozen at intake. The result retains
  that context and names the initiating device to receive focus.
- Unresolved evidence wins over a safe partial projection at the same anchor, yielding exactly one
  review decision instead of a contradictory action and review.
- Current, complete net-price and occupied-square evidence produce a separate value-per-square
  projection. Both flea and trader roles must be either a complete value or a trustworthy explicit
  `Unavailable` result, and both named roles remain in the calculation lineage. Opportunity cost
  remains the cost of choosing a non-economic need and is never treated as item price.
- The planner orders candidates by the versioned recommendation precedence and then supported net
  value. Accepted placements mutate one in-memory capacity plan so later items cannot reuse them.
- A swap removes at most three exact visible carried footprints, never a protected, pinned,
  stale, low-confidence, mismatched, or unknown item. Replacement cost equals the sum of the
  displaced evidenced values.
- Changed content, partial carried coverage, ambiguous identity or footprint, stale evidence,
  unsupported ruleset, or mismatched binding produces review rather than a guessed action.
- Scan and carried-item counts are bounded, swap search streams its best candidate, and caller
  cancellation interrupts placement work.
- Recommendation inputs, generated reasons, alternatives, and provenance traversal share a
  bounded, cancellable work budget. Expected per-item evaluation failures become REVIEW without
  aborting the rest of the screenshot.
- Only exact reason codes emitted by active raid, scarcity, need, and economics rules are accepted;
  unknown provenance is rejected recursively. The service checks generated action, economic band,
  sale channel, raid threshold, contract version, and request identity before planning capacity.
- A decisive result requires current, scored provenance throughout each reason and opportunity-cost
  lineage. A value-per-square lineage that cannot be wrapped inside the evidence contract becomes
  review-only instead of throwing.

The native review surface progressively discloses evidence and alternate identities. It keeps the
verdict, reason, value, value per square, footprint, swap cost, and named drop plan in the primary
scan path.

## Consequences

- TAKE, SWAP, LEAVE, and REVIEW form one cumulative plan for the reviewed screenshot instead of a
  set of mutually impossible individual fits.
- Redecode and paired-device delivery can reject stale same-cell advice without retaining image
  bytes after review.
- The planner remains deterministic and platform-independent; Avalonia and the paired web client
  consume the same result rather than recomputing capacity.
- Raid-risk and scarcity/obtainability inputs use the recommendation rules integrated by #274;
  shell/tablet composition remains owned by #287 and #290.
