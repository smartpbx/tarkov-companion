# Concept render audit

> **Status: not yet run with participants.** This is an expert review of the eight images in
> `docs/design/v2/` by the author of this package, on 2026-09-14. It records what is visible in each
> image and why it matters for v2. It is not usability evidence and no participant has seen these
> findings. The images are unchanged; nothing here edits or replaces them.

The renders are directional hypotheses, and [the concept notes](../README.md) already say prices,
counts, names, confidence values, routes and the QR code are illustrative. The point of this audit is
not that sample numbers are "wrong". It is that a render teaches implementers a pattern, and several
patterns here would ship a contradiction, an unlabelled claim, or a boundary misunderstanding if
copied. Each item names the render, what is visible, and the risk.

**Risk tags:** `truth` (implies more certainty than the data has) · `boundary` (could read as
something outside the fixed boundary) · `contradiction` (two parts of the product disagree) ·
`missing` (a required surface or state is absent) · `a11y` (accessibility) · `nav` (information
architecture) · `keep` (a pattern worth preserving).

Every non-`keep` item maps to a numbered item in [revision-brief.md](revision-brief.md).

## Cross-cutting

| ID | Visible in | Observation | Risk | Brief |
| --- | --- | --- | --- | --- |
| RA-X1 | All | **No Debrief concept.** "Debrief" is a rail item on six desktop renders; no render shows it. Post-raid review, correction, and preserved predictions have no visual direction. | `missing` | RB-01 |
| RA-X2 | All desktop | **No full Setup & Admin concept.** The only entry is an unlabelled gear icon at the bottom of the rail. The Home render shows a readiness checklist but none of the ten sections #256 names (Setup, Game & Profile, Recognition, Data, Team, Updates, Privacy, Appearance, Accessibility, Diagnostics). | `missing` `nav` | RB-02 |
| RA-X3 | Home | **Home is hidden and ambiguous.** The page titled "Ready when you are" highlights **Raid** in the rail. There is no Home destination, so the render does not say how a player returns to it or whether Raid *is* Home. | `nav` | RB-03 |
| RA-X4 | Home, Raid, Intel, Plan, Team tablet | **Capture is not global.** Capture appears only inside Loot Scan ("Scan again"), Stash Scan ("Add screenshot") and the tablet control render ("Contextual capture"). #256 requires Capture from Raid, Intel, Plan, Team and the tablet. | `missing` `nav` | RB-04 |
| RA-X5 | All six desktop | **"Ready" contradicts the page.** A green "Ready" pill sits top right on every desktop render, including Home, where System health says "1 needs attention" and Profile selected says "Action needed", and Stash Scan, where a scan is at 7 of 9. #256 lists "Data Current" and "Scan Ready" overclaiming as a v1 defect. | `truth` `contradiction` | RB-05 |
| RA-X6 | Raid, Intel, Plan, Stash, Loot, both tablets | **The clock is unlabelled.** "18:42" beside a clock icon appears on pages with and without a raid; Raid's own phase control reads 0:00; both tablet renders show a device status-bar 18:42 as well. It could be local time, raid time, or time left. #261 (raid clock provenance) is a permanent regression fixture. | `truth` `contradiction` | RB-06 |
| RA-X7 | Home, Raid, Intel, Plan, Stash | **"Data updated 12 min ago" does not say which data.** Loot Scan splits "Screenshot analysed 1.4 sec ago" from "Prices updated 12 min ago", which is better, so the renders are inconsistent with each other. | `truth` | RB-06 |
| RA-X8 | All | **No degraded states.** No render shows empty, loading, offline, stale, partial, permission denied or failed, although #256 requires each to be distinct and actionable. | `missing` | RB-07 |
| RA-X9 | All | **No accessibility variants.** No high-contrast, 200% text, narrow window, keyboard focus or screen-reader annotation. Several states rely on colour: member presence dots, amber versus green checks, Keep and Sell box outlines, and the traffic gradient. | `a11y` `missing` | RB-08 |
| RA-X10 | All | **Sample facts are not labelled in the images.** Photographic map and item imagery, quest names, member names (including a real first name and a real computer name), "Operation Railbird", and every price and count read as real. Home's "Dorms key spawn rate increased" and "New extract timing data (RUAF)" are fact-shaped claims with no source. | `truth` | RB-09 |
| RA-X11 | Home, Raid, Plan | **Traffic provenance is incomplete and inconsistently worded.** Raid shows "MODELLED TRAFFIC · NOT LIVE", "1,284 historical raids" and "Data through Sep 12", but no source, generated time, model version, or what the confidence means. Plan shows "Estimated traffic · historical, not live" with no provenance at all. Home shows "Customs traffic model updated" with no version. | `truth` | RB-10 |
| RA-X12 | Raid, Plan, Intel, Stash, Loot | **Different confidences look identical.** "Confidence 72%" (Raid model), "Confidence 72%" (Plan route), "Recommendation confidence 91%" (Intel), "Confidence 94%" (Loot detection), "96% confidently identified" (Stash). Same format for a model calibration, a route estimate, a rule outcome, a detection, and an identification rate. | `truth` | RB-10 |
| RA-X13 | Plan, Stash | **A second navigation control.** "Plan ▾" and "Stash Scan ▾" dropdowns in the top bar duplicate the rail. | `nav` | RB-03 |

## 1. Home and setup (`v2-home-setup-concept.png`)

| ID | Observation | Risk | Brief |
| --- | --- | --- | --- |
| RA-H1 | Headline "Ready when you are" and the "Ready" pill while one of four setup steps (Profile & wipe) is incomplete. | `truth` | RB-05 |
| RA-H2 | "Current plan · Customs · 4 objectives · Lower-contact route ready" exists before a profile is chosen, so it is unclear whose quests the plan is built from. | `contradiction` | RB-11 |
| RA-H3 | Status and action share one button shape: "Connected", "Ready", "Choose profile" and "Optional" all look like buttons. A player cannot tell a status from a control. | `a11y` `nav` | RB-11 |
| RA-H4 | "Diagnostics: Local only · No personal data is collected" is an absolute claim; positions, raid history and team presence are personal. #256 asks for a privacy inventory instead. | `truth` | RB-12 |
| RA-H5 | "Screenshot cleanup: Off · Screenshots stay on your PC" is good, but nothing says what happens to the image the companion analyses, so cleanup and analysis retention can be confused. | `truth` | RB-12 |
| RA-H6 | "Use sample data" has no visible sample mode indicator elsewhere in the set. | `truth` | RB-09 |
| RA-H7 | Readiness checklist, privacy panel, recent intelligence with dates. | `keep` | — |

## 2. Raid intelligence (`v2-raid-intelligence-concept.png`)

| ID | Observation | Risk | Brief |
| --- | --- | --- | --- |
| RA-R1 | **Route and extract disagree.** The highlighted cyan route runs from ZB-1011 towards RUAF Roadblock with arrows pointing to RUAF, and the legend says "Primary route (lower contact) ~14–18 min". Extract options mark **ZB-1011** "Primary ~14–18 min" and RUAF Roadblock "~16–20 min". The render does not say where the primary route ends. | `contradiction` | RB-13 |
| RA-R2 | "Avoids early Dorms convergence" while the primary route passes beside the Dorms marker through a red-orange area. | `contradiction` `truth` | RB-13 |
| RA-R3 | "Uses terrain and cover effectively" is not tied to any input the render shows. #256 requires each route trade-off to say which data produced it; a cover judgement needs a named, versioned source or should not be stated. | `truth` | RB-13 |
| RA-R4 | The traffic legend is a colour gradient only ("Lower traffic" to "Higher traffic"). | `a11y` | RB-08 |
| RA-R5 | "Scav Checkpoint" is offered as an extract while the header says PMC. v1 deliberately hides exits the current side cannot take; confirm against the catalog before a revised render repeats it. | `contradiction` | RB-13 |
| RA-R6 | Header shows "CUSTOMS · PMC · PVP" and "Start plan", but no raid state (not in raid, loading, in raid), so it is unclear whether this is planning or live raid use. | `nav` `truth` | RB-06 |
| RA-R7 | "MODELLED TRAFFIC · NOT LIVE" banner over the map, sample size, data-through date, phase scrubber, independent layer toggles. | `keep` | — |

## 3. Intel (`v2-intel-workspace-concept.png`)

| ID | Observation | Risk | Brief |
| --- | --- | --- | --- |
| RA-I1 | **Gross or net is not stated.** "₽892,450 Flea market · Updated 12 min ago" with no fee or net, "Flea market avg." is the same ₽892,450, and "Best sale: Flea market" implies proceeds. | `truth` | RB-14 |
| RA-I2 | "₽124,740 Therapist · 30-day range" labels a single price as a range. | `contradiction` | RB-14 |
| RA-I3 | Source comparison shows Mechanic ₽182,000 above Therapist ₽124,740, yet Key info gives "Vendor sell price ₽124,740 (Therapist)". The best trader is inconsistent. | `contradiction` | RB-14 |
| RA-I4 | "Found in raid: Yes" on an item-type page. Found-in-raid belongs to a particular item a player holds, not to the catalog item. | `truth` | RB-14 |
| RA-I5 | "Quest use: No" and "0 needed for active quests", but "Used in: Import · Quest". Either a contradiction or an unlabelled future-quest need. | `contradiction` | RB-14 |
| RA-I6 | "Data coverage 9/10 facts known" and "Recommendation confidence 91%" side by side; the relationship is not explained. | `truth` | RB-10 |
| RA-I7 | Description "A favored target for both crypto farmers and technical enthusiasts" is flavour text, not a sourced fact. | `truth` | RB-09 |
| RA-I8 | "Mark as owned" suggests a manual owned count, while #256 restores owned counts as observed stash-scan snapshots with coverage and time. The render does not show which. | `truth` | RB-14 |
| RA-I9 | Header shows Customs, PMC · PVP and 18:42 on a page that is not about a raid. | `nav` | RB-06 |
| RA-I10 | Named missing source ("1 barter source unavailable"), price history, source comparison, "Used in" links. | `keep` | — |

## 4. Plan (`v2-plan-workspace-concept.png`)

| ID | Observation | Risk | Brief |
| --- | --- | --- | --- |
| RA-P1 | Requirements "All ready" with green checks and no source for how any item is known to be owned. The Team render shows Food & water **unchecked** for the same "Customs progression" plan. | `truth` `contradiction` | RB-15 |
| RA-P2 | "Objectives (4)" includes "Extract at RUAF Roadblock"; the bundle card says "4 objectives · 3 quests". An extract is counted as an objective. | `contradiction` | RB-15 |
| RA-P3 | "Lower expected contact" while the route crosses orange areas at both bridges, with no trade-off explanation (Raid has "Why this route"; Plan has none). | `truth` | RB-13 |
| RA-P4 | "Estimated traffic · historical, not live" with no provenance (RA-X11). | `truth` | RB-10 |
| RA-P5 | "Confidence 72%" is the same figure as Raid's model confidence, for a different route and estimate (24–29 min against 14–18 min). | `truth` | RB-10 |
| RA-P6 | "Share with team" offers no scope choice. #256 requires explicit scope: private, paired devices, team. | `missing` | RB-15 |
| RA-P7 | Loadout "8/8 slots ready · ₽284,600 · 31.4 kg" with no coverage; #256 requires coverage beside aggregates, and missing price or weight never counted as zero. | `truth` | RB-15 |
| RA-P8 | Suggested bundles, numbered objectives matching numbered map points, "Open in Raid". | `keep` | — |

## 5. Team on tablet (`v2-team-tablet-concept.png`)

| ID | Observation | Risk | Brief |
| --- | --- | --- | --- |
| RA-T1 | **Role conflict.** "This tablet · Observer role" alongside "Invite a device", "Copy invite", "Show QR" and "Manage access". An observer in #256 is view-only. | `contradiction` | RB-16 |
| RA-T2 | A member card "Clayton · Owner · Ready" and "This tablet · Observer role" do not say whether the tablet is Clayton's paired device or another person. #256 forbids a paired device appearing as a phantom member. (Render 6 supersedes this render for device control, but the image still teaches the ambiguity.) | `boundary` `contradiction` | RB-16 |
| RA-T3 | "3 of 4 ready" lists only objectives 1 and 4. | `contradiction` | RB-15 |
| RA-T4 | Team objective 1 is "Meet at Warehouse 4", but map waypoint 1 is "Factory Far Corner". | `contradiction` | RB-15 |
| RA-T5 | Presence uses three vocabularies ("Ready", "Active", "2 min ago") and colour dots; stale is conveyed by an amber dot. | `a11y` `truth` | RB-08 |
| RA-T6 | Small touch targets and text: the member overflow "⋯" button and the map scale labels. | `a11y` | RB-17 |
| RA-T7 | The QR code and "Expires in 09:42" look functional (the concept notes say decorative). | `truth` | RB-09 |
| RA-T8 | "Shared plan · manual waypoints" label, expiring invite, session expiry, "Private relay". | `keep` | — |

## 6. Tablet controlling the desktop (`v2-tablet-desktop-control-concept.png`)

| ID | Observation | Risk | Brief |
| --- | --- | --- | --- |
| RA-C1 | **Control and Independent contradict.** "Control desktop" is selected, yet the primary button is "Show this view on desktop", which #256 assigns to Independent view; in Control mode changes apply to the desktop directly. The map badge "Same map and state as desktop" describes Follow. | `contradiction` | RB-18 |
| RA-C2 | Layers: "Traffic" is checked, but no traffic is drawn and no modelled label or provenance appears. | `truth` | RB-10 |
| RA-C3 | Layers: "Team marks" is unchecked, yet a mark "Added by Clayton · 4 sec" is shown. | `contradiction` | RB-18 |
| RA-C4 | "This tablet · Owner" here, "Observer role" in render 5, for the same kind of paired tablet. | `contradiction` | RB-16 |
| RA-C5 | No revision, acknowledgement or conflict state: only "Synced just now". #256 requires applied-revision confirmation and visible conflict resolution. | `missing` | RB-18 |
| RA-C6 | Small touch targets: undo, redo and overflow at the map's lower right, and the layer checkboxes. | `a11y` | RB-17 |
| RA-C7 | Real-looking identity: "PAIRED TO CLAYTON-PC", "Added by Clayton". | `truth` | RB-09 |
| RA-C8 | Persistent "Desktop now showing Raid · Customs · Floor 1", mode segmented control, "Loot scan armed · Waiting for your game screenshot", "Controls companion state only · No game input". | `keep` | — |

## 7. Stash Scan (`v2-stash-scan-concept.png`)

| ID | Observation | Risk | Brief |
| --- | --- | --- | --- |
| RA-S1 | **Counts conflict.** "96% confidently identified" while Review holds 24 of 218 stacks; "Keys · 23 of 25 identified" while Suggested organisation says "Review 6 uncertain keys"; "Hideout · 31 still needed" carries a completed "31/31 ✓". (Keep 64 + Sell 89 + Use soon 41 + Review 24 = 218 does reconcile.) | `contradiction` `truth` | RB-19 |
| RA-S2 | **The layout implies automated sorting.** "Reconstructed stash" groups items into Ammo case, Key tool, Quest items and Sell boxes, which reads as the stash already rearranged by category, not as where items were observed. | `boundary` `truth` | RB-19 |
| RA-S3 | "7 of 9 screenshots · 78%" does not explain how the total of 9 is known, and a screenshot percentage is not stash coverage. | `truth` | RB-19 |
| RA-S4 | **"Source images discarded after analysis" is ambiguous:** it can be read as the companion deleting the player's EFT screenshot files. | `truth` `boundary` | RB-12 |
| RA-S5 | "Estimated stash value ₽18.6M · 94% price coverage" during an incomplete scan, with gross or net unstated. | `truth` | RB-14 |
| RA-S6 | "Finish scan" is primary while "Capture remaining area" is unchecked; the consequence of finishing with partial coverage is not stated. | `truth` | RB-19 |
| RA-S7 | Stash Scan sits under the Intel rail item, with a separate "Stash Scan ▾ · FULL STASH" top-bar control. | `nav` | RB-03 |
| RA-S8 | "Watching EFT screenshots" uses a spinner as the only activity indicator. | `a11y` | RB-08 |
| RA-S9 | Guided next step ("Next: scroll down one screen"), overlap handling, "Planning only · No game input is generated". | `keep` | — |

## 8. Loot Scan (`v2-loot-scan-concept.png`)

| ID | Observation | Risk | Brief |
| --- | --- | --- | --- |
| RA-L1 | **Six items, five decisions.** "Container contents · 6 items (16/48 squares)", but "What should I take?" covers five and omits the Military cable tile (₽12k/sq); the summary "Take 3 · Swap 1 · Leave 1" totals 5. | `contradiction` | RB-20 |
| RA-L2 | **KEEP and TAKE are mixed.** Fuel conditioner and Virtex are "KEEP" although they are in the container, not carried; Bolts in the same container is "TAKE". No REVIEW decision is shown. | `contradiction` | RB-20 |
| RA-L3 | **Free space does not add up.** The backpack reads "8 × 6 squares" and "5 free squares", but the hatched empty region is visibly larger, and no fit statement shows that the takes and swap fit. | `contradiction` | RB-20 |
| RA-L4 | **The swap is impossible as drawn.** "Best swap: Leave Crickent → Take Electric drill · +₽92k". Crickent is in the container, so leaving it frees no backpack space. The panel names "Lowest carried: Wires · ₽12.4k/sq", which is what a swap would drop. +₽92k does not reconcile with a ₽32k/sq drill. | `contradiction` `truth` | RB-20 |
| RA-L5 | "SWAP · Electric drill · Higher total value" while its ₽32k/sq is lower than two KEEP items; the reason is not explained. | `truth` | RB-20 |
| RA-L6 | Per-square prices ("₽68k / square") with no gross or net and no per-item age. | `truth` | RB-14 |
| RA-L7 | "Scan again" suggests the companion takes a new screenshot itself. It can only wait for the player's next screenshot or re-analyse this one. | `boundary` | RB-04 |
| RA-L8 | Loot Scan sits under the Intel rail item during an in-raid Customs PMC context. | `nav` | RB-03 |
| RA-L9 | Rendering artefacts: "Why these results" is overlapped and partly garbled; the lower-left container thumbnail blurs into its label. "Quest profile: Clayton · PVP" uses a real name. | `truth` | RB-09 |
| RA-L10 | "Loot screen detected · Container + backpack · Confidence 94%", "Screenshot analysed 1.4 sec ago", "Advice on your second screen · No game input", value per square, filter chips. | `keep` | — |

## How this audit was produced

Each image was viewed in full at its committed resolution. Observations describe visible text and
layout only; where an observation depends on game knowledge (RA-R5) it says to confirm against the
catalog rather than asserting. No image was edited, regenerated, cropped into another file, or
replaced.
