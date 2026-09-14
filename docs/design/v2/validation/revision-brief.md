# Concept revision brief

> **Status: not yet run.** This brief comes from the expert audit in [render-audit.md](render-audit.md),
> not from participant sessions. Items marked **after validation** must wait for the decision log.
> No new image has been produced for this brief.

For whoever runs the next concept pass, by any method. The eight current renders stay where they are,
unchanged, as the record of the first pass. Revised or additional images go to new files, named so
they cannot be mistaken for the originals, in a location chosen by the design owner. They are outside
this issue's owned paths, so this pull request adds none.

## Ground rules for every revised image

1. **Label sample content in the image.** A visible "Sample data" marker on every screen, neutral
   sample names (no real people, machines or gamer tags), and no fact-shaped claim without a source
   line. Photographic map and item imagery stays marked as placeholder.
2. **Numbers must reconcile inside the image and across images.** Counts sum, space fits, swaps
   free the space they claim, the same plan has the same requirements everywhere, and a percentage
   says what it is a percentage of. Use the reconciled sample set in
   [prototype/content.js](prototype/content.js) unless there is a reason not to.
3. **Every modelled layer carries its complete evidence without policy clutter**: keep a compact
   "Modelled traffic · not live" context label inline; show freshness/confidence inline only when
   material; put source, data through, generated, coverage, confidence meaning, model version and
   Why in adjacent on-demand details. Link the full Safety and data methodology from Setup & Admin,
   About, or Data and Privacy (C-07).
   One wording everywhere.
4. **No single "Ready".** Status is counted and specific ("1 setup item needs action"), with each
   dependency's own time.
5. **Times say what they are.** "Local 18:42" and "Raid elapsed 12:30 (game log)" are different
   labels. Never an unlabelled clock.
6. **Prices say gross, fee, net and age.** Advice uses a named basis ("flea net est."). An estimate
   is never worded as proceeds.
7. **Capture copy is exact.** "The screenshot file stays in your EFT folder. The decoded image was
   discarded after analysis." Never "source images discarded".
8. **Status is not a button.** Statuses are text with a symbol; actions are buttons.
9. **Nothing depends on colour alone.** Text, shape, pattern or position carries every state.
10. **Tablet targets at least 44 by 44 px**, primary tablet controls about 48 px high.

## New concepts required

| ID | Concept | Must show | From |
| --- | --- | --- | --- |
| RB-01 | **Debrief** (or History, after validation) | Raid list; a timeline where every row says Observed, Inferred, Estimated or Manual and its source; carried value "estimated at capture, not extracted"; a recognition correction with Undo; a failed save keeping a draft; the traffic prediction preserved as shown with its model version; "higher, as shown, lower" feedback; export | RA-X1 |
| RB-02 | **Setup & Admin** in full | The ten #256 sections as labelled navigation; readiness with last attempt, last success, next retry, source and exact reason; privacy inventory; screenshot folder cleanup as opt-in with preview, separate from capture analysis; Debug Capture off with expiry and redaction; diagnostics preview before export; accessibility and appearance settings | RA-X2 |
| RB-03 | **Home and navigation model**, **after validation** | Either Variant A (rail with labelled Setup & Admin, no Home) or Variant B (Home, Raid, Prepare, Team, History with contextual Intel), as decided in [../decision-log.md](../decision-log.md) (H-01 to H-07). One navigation control only: no duplicate top-bar dropdowns. Stash Scan and Loot Scan placed where the decision says | RA-X3, RA-X13, RA-S7, RA-L8 |
| RB-04 | **Global Capture** (header placement provisional until H-04) | Capture reachable from every desktop page and the tablet (#256 asks for Capture from each workspace; the header is this package's proposal): what is armed, its revision and device, "N captures need a decision" with Decide, recent captures. Loot Scan's "Scan again" replaced by "Waiting for your next screenshot" or "Analyse this screenshot again" | RA-X4, RA-L7 |
| RB-07 | **Degraded-state sheet** | For each workspace, one frame each of empty, loading, offline, stale, partial, permission denied and failed, using [state-matrix.md](state-matrix.md): what still works, provenance, recovery action | RA-X8 |
| RB-08 | **Accessibility sheet** | Windows high contrast on Raid and Loot Scan; 200% text on Loot Scan; a narrow docked window (about 480 px) beside the game; visible keyboard focus on a dialog; traffic legend with pattern and text; presence and stale shown as words; stage progress without a spinner | RA-X9, RA-R4, RA-T5, RA-S8 |
| RB-17 | **Tablet touch sheet** | Tablet control and Team at real size with every target at least 44 px, overflow and undo controls included, one-handed portrait layout | RA-T6, RA-C6 |

## Corrections to existing concepts

| ID | Applies to | Change | From |
| --- | --- | --- | --- |
| RB-05 | All desktop, Home, Stash | Remove the global "Ready" pill. Replace with a counted status (wording provisional until H-15) that links to its detail; during a scan show the scan's state, not "Ready" | RA-X5, RA-H1 |
| RB-06 | All with a clock or "Data updated" | Separate local time from raid elapsed or remaining time with their source; name the data behind every "updated" line; show raid state (not in raid, loading, in raid) on Raid; show map and side context only where relevant | RA-X6, RA-X7, RA-R6, RA-I9 |
| RB-09 | All | Apply ground rule 1: sample marker, neutral names, sourced claims, visibly decorative QR, remove rendering artefacts ("Why these results") | RA-X10, RA-H6, RA-I7, RA-T7, RA-C7, RA-L9 |
| RB-10 | Home, Raid, Plan, Intel, tablet control | Apply ground rule 3 everywhere a model is drawn or referred to; give each material confidence a label saying what it measures (model calibration, detection, identification rate, rule outcome); keep complete evidence in adjacent Why/details; draw a checked Traffic layer with its compact context label, or uncheck it | RA-X11, RA-X12, RA-I6, RA-P4, RA-P5, RA-C2 |
| RB-11 | Home | Make "Current plan" depend visibly on the chosen profile (or show it as sample); distinguish statuses from buttons | RA-H2, RA-H3 |
| RB-12 | Home, Stash, all capture surfaces | Replace "No personal data is collected" with an inventory link; use ground rule 7 wording; show cleanup and analysis retention as separate lines | RA-H4, RA-H5, RA-S4 |
| RB-13 | Raid, Plan | Make the primary route end at the primary extract, with one estimate; make route reasons match the drawn heat and name the data behind each; add Plan's trade-off explanation; list only extracts available to the current side, marked observed, catalog-possible or conditional | RA-R1, RA-R2, RA-R3, RA-R5, RA-P3 |
| RB-14 | Intel, Loot, Stash | (Value wording provisional until H-09) Apply ground rule 6; label a range as a range; make the best trader consistent with the comparison; drop "Found in raid" from the item type (show it on held items only); state current or future quest use explicitly; show owned counts as "observed N in stash scan at time, coverage" or "entered by you" | RA-I1 to RA-I5, RA-I8, RA-L6, RA-S5 |
| RB-15 | Plan, Team tablet | One requirement truth for one plan, each with its source; extract separate from objectives; objective counts match the list; waypoint numbers match objectives; share with an explicit scope; loadout totals with coverage | RA-P1, RA-P2, RA-P6, RA-P7, RA-T3, RA-T4 |
| RB-16 | Team tablet, tablet control | The paired tablet is always "your device, not a squad member" (wording provisional until H-12); its permissions match its role everywhere (an observer cannot invite or manage access); one role for the same device across renders | RA-T1, RA-T2, RA-C4 |
| RB-18 | Tablet control | Control desktop shows changes applied with a confirmed revision and no "Show this view on desktop"; that button appears only in Independent view; Follow's badge only in Follow; marks visible only when their layer is on; add a "desktop changed first" conflict frame | RA-C1, RA-C3, RA-C5 |
| RB-19 | Stash Scan | Show coverage as rows or area covered, not screenshot count; make identification, review and key counts agree; show observed layout separately from a manual organisation plan labelled "you move the items"; state what finishing with partial coverage means; value as an estimate with basis and coverage | RA-S1 to RA-S3, RA-S6 |
| RB-20 | Loot Scan | (TAKE, SWAP, LEAVE, REVIEW and "flea net est." wording provisional until H-08 and H-09) One decision per container item (including REVIEW), summary counts that total the container; TAKE for container items, KEEP only for carried items; a fit statement ("4 free as one 2×2 block; after moves 0 free"); swaps drop a carried item and state the gain as net estimate difference; explain why a lower per-square item is swapped in | RA-L1 to RA-L5 |

## What to keep

The audit's `keep` rows are deliberate: the readiness checklist and privacy panel (RA-H7), the
"not live" context label, sample size and phase scrubber (RA-R7), named missing sources and "Used in"
(RA-I10), numbered objectives on the map (RA-P8), expiring invites and session expiry (RA-T8), the
persistent "Desktop now showing" line, mode control, armed-capture line and "companion state only"
note (RA-C8), guided next capture with compact manual-action context (RA-S9), and detected context
with confidence and analysis time available in details (RA-L10).
