# Guided full stash scan

The full-stash workflow consumes reviewed results from the contextual capture session. It never
owns screenshot pixels and never controls the game.

## Session flow

1. The player selects the Stash intent and supplies a scrolling sequence.
2. Capture review resolves or preserves each frame's item candidates and grid reconstruction;
   every frame retains the session's frozen capture context and its own correlation id.
3. `StashScanWorkflow` accepts the complete ordered batch in one application action.
4. Exact duplicate content is rejected process-locally. The content digest is discarded with the
   batch input and is absent from the recognition result, export, and database.
5. `StashScanAssembler` places confirmed/reviewed origins, then stitches only a unique overlap
   supported by at least two resolved anchors.
6. The workflow saves a typed, pixel-free `StashRecognition` through the existing observed
   inventory tables. The session report carries targeted retry guidance.

The durable snapshot id, recognition snapshot id, and catalog/economics data snapshot id remain
separate. The workflow persists and exports the requested data snapshot id so later price and
recommendation explanations can be reproduced against the same data publication.

An unplaced frame remains in the recognition result with an absent origin so its candidates and
capture ordinal survive review, but its items do not enter exact counts or known-value totals.

## Guidance codes

| Condition | Next step |
| --- | --- |
| First frame has no origin | Return to the container start. |
| Overlap is absent | Capture again with more overlap. |
| Two offsets tie | Review the conflicting cells or supply an origin. |
| The same absolute cell changed | Stop moving items, then rescan the affected range. |
| Declared cells remain uncovered | Capture the missing scroll range. |
| Cells are occluded or partial | Retry that visible region. |
| A nested container is closed | Open it and run a guided child scan. |
| Frames arrived out of ordinal order | Review the sequence; retained ordinals are not renumbered. |

## Review and planning

Comparison does not turn confidence into state. `Observed` means directly placed evidence,
`Inferred` means a deterministic stitch, and `Unresolved` means the snapshot cannot place or name
the occurrence. Additions/removals use `Absent` only for the side with no occurrence.

Correction, merge, split, pin, ignore, and rescan enter through append-only `StashReviewCommand`
records. They are review intent, not game actions. The organization planner consumes an already
computed v2 recommendation. Its only outputs are Keep, Sell, Use soon, Organize, and Review plus
manual checklist operations. Missing #308 ammo/key intelligence always produces Review.

## Snapshot lifecycle

Snapshots are scoped by exact profile id, generation, and game mode. List and export return typed
evidence without screenshot pixels or private content digests. Deleting the current
snapshot promotes the newest surviving record in the same scope. Retention requires an explicit
UTC cutoff, supports a non-mutating dry run, and cannot delete the current snapshot.

## Release evidence

Synthetic fixtures prove orchestration and policy, not EFT recognition accuracy. A release may
describe the full-stash recognizer as validated only when a versioned, held-out, reviewed corpus
records all of these gates for the exact producer version:

- resolved item-occurrence precision of at least **98%**;
- resolved item-occurrence recall of at least **95%**;
- placed-origin precision of at least **99%**;
- **zero** double-counted occurrences after overlap stitching; and
- **zero** invented entries from closed or unobserved nested containers.

Every evaluation also reports unresolved rate, movement-conflict count, corpus coverage by
resolution/UI scale/theme/localization, and the duplicate count before and after stitching. A
corpus without those measurements is unmeasured, not passing. Thresholds are release gates rather
than claims that the current synthetic fixtures meet them.

## V2 workspace

`Views/V2/StashScan` is the first caller of this backend outside its own tests. It registers
`StashScanWorkflow` and its dependencies in `AppComposition.cs`, lists and browses persisted
snapshots, and shows an ammo and key summary by joining recognized items against
`IItemFactCatalog`. Starting a scan arms the shell's existing capture chrome with the Stash, Ammo,
or Keys intent.

`StashScanCaptureHandoff` (#273) bridges an accepted Stash-intent capture into this backend: it
takes the pixel-derived `GridReconstructionRequest` `CaptureRecognitionPipeline` attaches to the
capture's analysis (see `docs/RECOGNITION.md`), reconstructs it, and assembles it as a single-frame
session rooted at the `stash` container path, confirmed to start at cell zero with an unresolved
total-cell count. Moving through container tabs, opening nested containers to capture them, and
supplying a real total-cell hint across an ordered multi-capture session are capture-lifecycle UI
this pass does not add; `StashScanAssembler` already stitches multiple frames when a future package
supplies them with origin hints, so nothing here needs to change to support that. Ammo and Keys
intents still have no handoff and are acknowledged without producing advice.
Keep/Sell/Use soon grouping via `StashOrganizationPlanner` and #308 ammo/key intelligence is not
wired either — general items are listed under Review.

## Safety

The feature is external and read-only relative to Escape from Tarkov. It uses only screenshots or
visible pixels the player requested. It never reads game memory, injects or hooks, inspects EFT
traffic, generates input, automates inventory/flea actions, tracks live players, or renders an
in-game overlay.
