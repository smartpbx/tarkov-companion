# V2 experience decision log

> **Status: not yet run.** No participant session has taken place, so **no hypothesis has been
> accepted or rejected on user evidence**. Entries below are either product-contract decisions
> already made on #256 (recorded here for traceability, not as validation results), method choices
> proposed by the #265 package, or open hypotheses awaiting sessions.

Owner: the #256 decision owner. Evidence lives in [validation/validation-report.md](validation/validation-report.md).
Method: [validation/research-plan.md](validation/research-plan.md).

## How entries move

| Status | Meaning | Who can set it |
| --- | --- | --- |
| **Contract** | Decided by the product owner on #256. Not a usability result. Changing it goes back to #256. | Product owner |
| **Proposed** | Put forward by this package, awaiting review by the decision owner or a contract owner | Anyone, with rationale |
| **Open** | A hypothesis the sessions will test | Research plan |
| **Accepted** | Supported by recorded evidence; rationale and evidence links required | Decision owner |
| **Rejected** | Contradicted by recorded evidence; rationale and evidence links required | Decision owner |
| **Deferred** | Deliberately not decided now; the reason, the risk, and what would decide it | Decision owner |

Every change adds a dated line to the entry's history. Nothing is deleted; a reversed decision gets a
new line saying so.

## Contract decisions already made (#256)

| ID | Decision | Source | Effect on #265 |
| --- | --- | --- | --- |
| C-01 | The fixed boundary: no EFT process memory, injection or hooks, EFT network inspection, generated gameplay input or automation, live enemy tracking, ESP or radar, or in-game overlay. | #256 contract; #265 shared gates | Every storyboard, script and probe keeps it. Any participant belief that the design crosses it is S0. |
| C-02 | Historical or modelled traffic is in scope only with source, timestamps, coverage, confidence and model version, and never as live detection. | #256 | Traffic appears in the storyboards only with full provenance and a "not live" label; H-10 tests whether that is read correctly. |
| C-03 | The tablet is a paired extension of the same user's desktop, with Follow, Control and Independent modes, never a phantom squad member. Supersedes the v1 conclusions of #217. | #256, 2026-09-14 | J5 and H-11, H-12 test the vocabulary, not the decision. |
| C-04 | Screenshot understanding is the primary contextual interaction; capture is user-driven with an explicit intent; disagreement enters review. | #256, 2026-09-14; #271 | J2, J3 and the capture specification test the flow, not the decision. |
| C-05 | Full Stash Scan and Loot Scan are in scope; sorting and swapping are advice for manual action only. | #256, 2026-09-14 | J2, J3. |
| C-06 | The workspace names Raid, Intel, Plan, Team, Debrief and Setup & Admin are a design direction, to be validated before navigation freezes. | #256 information architecture | This is why H-01 to H-07 exist. |

## Method choices proposed by the #265 package

| ID | Proposal | Rationale | Status |
| --- | --- | --- | --- |
| M-01 | Test two navigation variants (A workspace rail; B workflow hub with contextual Intel) over identical sample content and identical task cards. | #265 asks for at least two variants where evidence shows ambiguity; the concept renders already show ambiguity (render audit RA-X3, RA-X13, RA-S7, RA-L8), and identical content means a difference can only come from navigation. | Proposed |
| M-02 | Use small semantic HTML storyboards, not Avalonia prototypes or the concept images. | Participants who use keyboards, screen readers, magnification or touch can take part now, without building native code before navigation is decided. They cannot prove native accessibility (#266, #279 own that). | Proposed |
| M-03 | Count only real players who did not author the materials and are not the decision owner; the pilot does not count. | Five authors or planners would validate their own assumptions. | Proposed |
| M-04 | Pre-register the variant comparison rules and report counts, never percentages. | With two or three people per variant, a rule written after the data could be fitted to it. | Proposed |
| M-05 | Keep all raw session material outside this public repository; commit only de-identified synthesis. | The repository and release feed are public (#256 review snapshot). | Proposed |
| M-06 | Retain raw recordings and notes for 30 days after sign-off. | Long enough to re-check a disputed finding; short enough to limit exposure. | Proposed |

## Design proposals needing a contract owner

These came out of writing the specifications. They are not validated and not contracts.

| ID | Proposal | Rationale | Owner to review | Status |
| --- | --- | --- | --- | --- |
| P-01 | A capture whose detected context disagrees, or is unknown, is queued as "needs a decision" and announced politely; the dialog opens only when the player chooses Decide. | The screenshot arrives while the player is in the game; a modal would steal focus and could bring the companion forward over the game. Found while building the storyboard, which first opened a modal and broke the state-matrix focus rule. | #271, #266 | Proposed |
| P-02 | An armed Ammo, Keys or Quest items intent **narrows** a detected stash or loot grid instead of disagreeing with it. | Players open a stash to find ammo; forcing a mismatch dialog for that would be noise. | #264, #271 | Proposed |
| P-03 | A capture binds to the intent revision in force when the file appeared, not when analysis starts. | Deterministic under device races; matches what the player armed when they pressed the key. | #271, #276 | Proposed |
| P-04 | While an ordered stash-session capture awaits a decision, later captures in that session wait as unread files, not decoded pixels. | Preserves stitching order without holding pixels. | #271, #283 | Proposed |
| P-05 | A low-confidence detection yields "couldn't tell", checked before comparison, so a guess never produces a mismatch. | Avoids asking the player to arbitrate between two things the companion is not sure of. | #264, #272 | Proposed |

## Hypotheses awaiting sessions

All **Open**. Definitions and evidence are in [validation/research-plan.md](validation/research-plan.md) §3.

| ID | Hypothesis (short) | Decides | Status | History |
| --- | --- | --- | --- | --- |
| H-01 | Tasks are placed where #256 groups them | Workspace grouping for #267 | Open | 2026-09-14 opened |
| H-02 | A or B has fewer wrong turns and less assistance | Navigation model for #267, #290 | Open | 2026-09-14 opened |
| H-03 | Home (B) versus Setup & Admin as first-launch landing (A) | First-launch destination for #292, #267 | Open | 2026-09-14 opened |
| H-04 | Header Capture is found from anywhere | Capture placement for #267, #271 | Open | 2026-09-14 opened |
| H-05 | Intel as a side panel with an address versus an Intel workspace | Intel model for #287, #267 | Open | 2026-09-14 opened |
| H-06 | Stash scan under Intel (A) or Prepare (B) | Stash entry for #283, #267 | Open | 2026-09-14 opened |
| H-07 | Plan or Prepare; Debrief or History | Labels for #288, #291 | Open | 2026-09-14 opened |
| H-08 | TAKE, SWAP, LEAVE, REVIEW read correctly | Decision vocabulary for #282, #274 | Open | 2026-09-14 opened |
| H-09 | "Flea net est." read as an estimate | Value wording for #274, #282, #287 | Open | 2026-09-14 opened |
| H-10 | Modelled traffic never read as live | Traffic presentation for #275, #286 | Open | 2026-09-14 opened |
| H-11 | Tablet modes, owner line and conflict understood | Tablet vocabulary for #290, #276 | Open | 2026-09-14 opened |
| H-12 | Paired tablet understood as own device | Device identity presentation for #290, #289 | Open | 2026-09-14 opened |
| H-13 | Mismatch resolved without analysing the wrong thing | Decision dialog for #271, #287 | Open | 2026-09-14 opened |
| H-14 | "File stays; decoded image discarded" understood | Privacy copy for #271, #292 | Open | 2026-09-14 opened |
| H-15 | Counted status trusted more accurately than "Ready" | Status pattern for #267, #281 | Open | 2026-09-14 opened |
| H-16 | Announced result with "Open result" beats auto-navigation | Result delivery for #271, #282, #283 | Open | 2026-09-14 opened |
| H-17 | Observed, Inferred, Manual, Estimated understood | Provenance labels for #291, #264 | Open | 2026-09-14 opened |
| H-18 | List alternative is discoverable and sufficient | Map alternative for #286, #288, #266 | Open | 2026-09-14 opened |

## Deferred

| ID | Question | Why deferred | Risk | What would decide it | Status |
| --- | --- | --- | --- | --- | --- |
| D-01 | Does an armed Loot decision stay armed for the rest of a raid, or expire after one capture? | The storyboards show only a persistent armed intent, and expiry belongs to #271's contract. Testing one version only would bias the answer. | Players may analyse a later screenshot with a stale intent, or re-arm constantly. | A native #271 prototype with both behaviours, tested in J2-style sessions. | Deferred |
| D-02 | Does the native application meet the accessibility flows? | Storyboards cannot produce UI Automation or Narrator evidence. | Treating storyboard results as accessibility evidence. | #266 and #279 evidence against UXF-A11Y fixtures. | Deferred |
| D-03 | What do revised concept images show for navigation? | Revision brief item RB-03 depends on H-01 to H-07. | Rendering a navigation model before evidence exists repeats the first pass's ambiguity. | Decisions on H-01 to H-07. | Deferred |

## Accepted

None. No session has been run.

## Rejected

None. No session has been run.
