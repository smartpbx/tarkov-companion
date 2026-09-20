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

### The sort plan's caller (2026-09-19)

The planner was complete and tested and nothing called it, so every item in every scan sat
under Review and three of the workspace's four plan tiles read a dash. `StashPlanSource` is the
caller. Each named tile is put to `ExplainableRecommendationEngine` as a stash question, from the
same facts the Loot Scan reads (`docs/LOOT_SCAN.md`): the profile's pins, wishlist and item
rules, outstanding quest and hideout needs, the flea net after its fee, what a trader pays. The
workspace sorts a snapshot when it is loaded, lists Keep first, then Sell, then Review, tags
sorted tiles on the grid, and gives each row one line of why.

Three things it deliberately does not do:

- **No holdings are handed to the engine.** The stash being sorted is the holdings. Subtracting
  it from a need counts an item against itself: three Salewas held against a quest that wants
  three would all read "need met, sell". So everything a need still wants is Keep for every
  copy, and the reason says how many are wanted. Telling the spare copies apart is not done.
- **Gear is not told to be sold.** The first real render put an ammo case full of ammo, an
  M4A1 and the player's armour under Sell, because no quest needed them. Whether a rifle is
  surplus depends on the loadouts the player means to run, which a price does not know. Weapons,
  attachments, armour, plates, helmets, headsets, rigs, backpacks, cases, meds and provisions
  wait under Review with what they would fetch (`StashSpecialistIntelligenceKind.Gear`), the way
  ammo and keys wait for #308. A case the scan opened and read into counts as gear whatever the
  catalog files it under: the source lists an Ammunition case as `barter` before `container`.
  What the player has pinned, wishlisted, protected or given a rule is sorted all the same.
- **A sell row is not the engine's sentence.** That sentence is the working (roubles across
  squares, the band, both channels). A row says where it sells and for how much.

A snapshot opened the next day is still sorted: the engine ages a footprint like a price, and
the squares an item covers have not changed overnight, so the footprint is restated when the
plan is made and keeps the original reading as what it was derived from.

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

`StashScanCaptureHandoff` (#273) bridges an accepted Stash-intent capture into this backend. While
a guided scan is collecting (below) the capture joins that scan; otherwise it
takes the pixel-derived `GridReconstructionRequest` `CaptureRecognitionPipeline` attaches to the
capture's analysis (see `docs/RECOGNITION.md`), reconstructs it, and assembles it as a single-frame
session rooted at the `stash` container path, confirmed to start at cell zero with an unresolved
total-cell count. Moving through container tabs, opening nested containers to capture them, and
supplying a real total-cell hint across an ordered multi-capture session are capture-lifecycle UI
this pass does not add; `StashScanAssembler` already stitches multiple frames when a future package
supplies them with origin hints, so nothing here needs to change to support that. Ammo and Keys
intents still have no handoff and are acknowledged without producing advice. General items are
sorted into Keep, Sell and Review by `StashPlanSource` (see "The sort plan's caller" above);
#308 ammo/key intelligence is still not wired, so ammo and keys stay under Review.

## Guided full-stash scan (package 40)

A stash is taller than a screen: 34 rows is three 1080p screens. `GuidedStashScanService` holds the
screenshots of one scroll-through until the player presses Finish, and says after each one what to
do next. Choosing **Full stash** and **Start scan** in the workspace starts it; Ammo and Keys still
go through the shared capture dialog as one screenshot.

- **Arming.** An armed intent lives fifteen seconds and is spent by one capture, so
  `GuidedStashScanArming` re-arms Stash whenever the capture service is idle, but only while the
  scan is actively being taken. A scan read back at start-up waits for **Keep going**, and ten idle
  minutes release the slot, so an abandoned stash scan cannot swallow a loot scan in the next raid.
- **Closing it halfway.** The scan is written, without pixels, to `stash-scan-in-progress.json`
  in the config folder after every screenshot and read back the next time the Stash workspace
  opens. It ends only by **Finish** or **Discard**. A file that no longer reads is renamed
  `.unreadable`, not deleted.
- **A mis-step.** The same screenshot twice is skipped. One that shares no rows with the rest is
  kept and reported ("scroll back up a little"), and is placed as soon as a later screenshot
  bridges the gap. **Undo last** takes one back.
- **Stitching.** `StashScanAssembler` aligns on two named items. `StashLayoutAligner` runs after
  it, for the frames it left unplaced, and aligns on the shape of the footprints instead: every
  compared cell must agree, at least three whole items must be shared, and a tie is left unplaced.
  Rectangles touching a screenshot's first or last row are not compared, because the viewport
  cuts items there.
- **One grid.** `StashReconstructionProjector` folds the placed screenshots into container
  coordinates and claims each cell once: named beats unnamed, whole beats edge-cut, and two
  different names for one anchor leave the tile unknown. The workspace draws that grid, and draws
  a tile nobody could name as `?` where it is.
- **Owned counts.** On Finish, `StashOwnedCountsApplier` writes the named items' counts to
  `PlayerProfile.OwnedItemCounts`, which is what the hideout requirements, quest item needs and
  the Keep verdict read. A scan with any unknown tile or unplaced screenshot only raises counts;
  only a scan with everything named and placed may lower one. Such an exact scan also records 0
  for an item a quest or hideout level needs that it did not see and that had no count at all, so
  the planning pages read "0" instead of "?"; a count already recorded for an unseen item is
  untouched (it may be in a case), and anything short of exact leaves "?" alone.

### What was measured, and on what

There is no real stash screenshot on the development host, so
`StashScanEndToEndMeasurementTests` paints a 34-row stash at the one measured geometry and scores
the real pixel reader, reconstructor, assembler and projector against the layout it painted. The
artwork is invented, so these numbers describe the plumbing, not the game.

| Step | Lattice exact | Footprints found | Spurious | Screens placed | Items correct |
| --- | --- | --- | --- | --- | --- |
| Before | 0 / 3 | 23 / 202 | 242 | 1 / 3 | 0 / 165 |
| Stash lattice from its known shape | 3 / 3 | 125 / 202 | 261 | 1 / 3 | 0 / 165 |
| Footprints from the lines between cells | 3 / 3 | 202 / 202 | 7 | 1 / 3 | 0 / 165 |
| Layout stitching | 3 / 3 | 202 / 202 | 7 | 3 / 3 | 0 named, 165 drawn as unknown |
| The same, with in-game tile fingerprints | 3 / 3 | 202 / 202 | 7 | 3 / 3 | 162 / 165, 0 wrong |

The seven spurious footprints are items the viewport really does cut; the folded grid replaces
them with the whole item from the neighbouring screenshot. A per-screenshot list of the last row
would have shown 36 items the stash does not contain.

When this was measured (package 40) two things kept a real scan from naming anything, both in
the shared recognizer: nothing fed the icon evidence cache, and `IconCandidateSeparator` named a
tile only on a bit-exact fingerprint, which the painted tiles matched 0 times in 67. Both have
since been replaced: `IconEvidenceIndexer` (#433) fills the cache from the synced catalog, and
a tile is named by the correlation of a colour pixel descriptor (`IconPixelDescriptor`), with
the difference hash kept only as a shortlist. What that names is in `docs/RECOGNITION.md`.

### The same code on real screenshots (2026-09-18)

Nine screenshots of a real stash arrived after the table above was written: 3840x1080, taken in
one burst at the hideout (menu screens, not a raid), with the gear panel, pockets, special slots
and a backpack grid on screen at the stash's own pitch, and on two of them a fourteen-column Junk
case opened in front. They live outside every checkout and are never committed.
`RealStashFrameMeasurementTests` runs over them and skips when they are absent; footprint truth is
the hand-read `<screenshot>.expected.json` labels the Loot Scan measurement writes beside them.

| | Painted frames | Real, code as merged | Real, after this change |
| --- | --- | --- | --- |
| Stash panel found, and it is the stash | 3 / 3 | 6 / 9 — nothing on a screen of large cases; the opened case taken for the stash twice | 7 / 7, and both case-covered screens refused |
| Whole rows only | 3 / 3 | 0 / 6 | 7 / 7 |
| Footprints against labels (six frames, 385 items) | 202 / 202 | 359 (93.2%), 106 spurious | 366 (95.1%), 8 spurious |
| Boundaries judged right (1,109 labelled) | — | not measured | 1,089 (the ridge alone: 1,070) |
| Screens placed, in the order captured | 3 / 3 | 1 / 7 | 1 / 7 |
| Pairs that do overlap, placed at the true row | 3 / 3 | not measured | 3 / 3 |
| Pairs with no whole row in common left unplaced | — | not measured | 6 / 6, none placed wrongly |

The burst still places one screen of seven, and that is the right answer for it: the player paged
the stash, so no later screen shares a whole row with the first, and nothing may be placed by
guessing. The pairwise rows are what show the stitch itself working.

Three things the painted frames had assumed were false, and each is now drawn the way it measured:

- **An item does not hide the grid inside it.** It shows through the transparent parts of large
  art at 8 to 10 levels. An item's border is a single pixel 25 to 45 over both neighbours, on the
  lattice line itself. Armour and backpacks sit on a pale tile whose border measures 1 to 5 and
  whose edge is a step of 50 to 65 instead. A shared side is a border at a median ridge of 12 or a
  median step of 40, chosen from every combination against the labelled boundaries.
- **An empty cell is not flat.** It carries a one-pixel hatch of about 6 levels, with grid lines
  15 over it, so it is judged on 5x5 block means.
- **Screens do not overlap by half.** Unguided, the player paged: most neighbours share only the
  row the viewport cuts. Those are rightly left unplaced, and the guided card's "scroll about half
  a screen" is what makes a scan stitchable. Where screens did overlap (by 3, 9 and 13 rows),
  comparing occupancy and border bits - which a viewport cut cannot corrupt - agreed on 251 of 251
  bits at the true offset and under 0.79 everywhere else.

What is left: borders between two dark weapon parts measure 8 to 10, the same as grid showing
through an item, so a row of scopes can merge (frame 2: 92 of 107). One mis-joined border used to
shatter a whole group into single cells; the strongest-evidence side inside an irregular group is
now cut until rectangles emerge. The scroll truth for stitching is the vertical shift that best
lays one viewport's pixels over the other's, believed only when it clearly beats every other
shift, so it judges the lattice and footprints without depending on them.

`tools/V2RenderPreview --stash-scan-demo mid|complete|mid-unnamed|complete-unnamed` drives the
composed services from the same painted frames.

## Safety

The feature is external and read-only relative to Escape from Tarkov. It uses only screenshots or
visible pixels the player requested. It never reads game memory, injects or hooks, inspects EFT
traffic, generates input, automates inventory/flea actions, tracks live players, or renders an
in-game overlay.
