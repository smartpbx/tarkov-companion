# Acceptance fixture map

> **Status: not yet run.** Every fixture below is a **draft** derived from the journeys and
> specifications in this package. None is validated. A fixture becomes binding for its implementation
> issue only after [validation-report.md](validation-report.md) is signed off and the decision log
> accepts the hypothesis behind it. Until then an implementation issue may reference a fixture as
> "draft, #265".

Fixtures are written as behaviour a native test, a Windows end-to-end scenario (#279), or a manual
accessibility check can assert. They name observable outcomes, not implementation. Where a fixture
depends on a navigation decision, it says **after H-xx** and has a version per variant until decided.

**Consumers** are the V2 issues expected to satisfy the fixture. **Evidence** is the kind of proof
expected: `unit` (domain or view-model test), `e2e` (#279 Windows scenario with rendered-state
assertion), `a11y` (manual assistive-technology check recorded under #266 or #279), `protocol`
(#276 golden vector or property test).

| Status values | Meaning |
| --- | --- |
| Draft | Written from the specification; not yet exercised by participants |
| Validated | Supported by the signed validation report; binding |
| Changed | Revised because of a finding; the finding ID is given |
| Dropped | Rejected by a decision; the decision ID is given |

## J1 First launch

| ID | Given / when / then | Consumers | Evidence | Status |
| --- | --- | --- | --- | --- |
| UXF-J1-01 | Given a first launch with one required check incomplete, the readiness summary states the count ("1 item needs action") and no surface shows an unqualified "Ready". | #292, #267, #281 | e2e | Draft |
| UXF-J1-02 | Every readiness check shows its status word, its own check time or last success, and one action; status is not rendered as a button. | #292, #281, #266 | e2e, a11y | Draft |
| UXF-J1-03 | Before any setup, sample data can be explored and every sample surface is labelled as sample. | #292, #267, #264 | e2e | Draft |
| UXF-J1-04 | The first-launch landing is **after H-03**: A Setup & Admin, Get ready; B Home. Completing the last required action announces once and returns focus to the control that opened the action. | #267, #292, #266 | e2e, a11y | Draft |

## J2 Loot decision

| ID | Given / when / then | Consumers | Evidence | Status |
| --- | --- | --- | --- | --- |
| UXF-J2-01 | Capture can be armed from every workspace and from the paired tablet; the armed intent, revision and device are visible until it changes or expires (expiry **needs contract**, #271). | #271, #267, #290 | e2e | Draft |
| UXF-J2-02 | For every visible container item there is exactly one decision: TAKE, SWAP, LEAVE or REVIEW; the summary counts total the container item count. | #282, #274 | unit, e2e | Draft |
| UXF-J2-03 | KEEP applies only to carried items; container items use TAKE. | #282, #274 | unit | Draft |
| UXF-J2-04 | A fit statement gives free squares and their shape before and after the recommended moves; no recommended move exceeds the visible free or replaceable space, accounting for size and rotation. | #282, #273, #274 | unit, e2e | Draft |
| UXF-J2-05 | A SWAP names the carried item to drop and states the gain as the difference of the named value basis (for example flea net estimates); the dropped item is the lowest-priority unprotected carried item. | #282, #274 | unit | Draft |
| UXF-J2-06 | Every value shows its basis (gross, estimated fee, net, trader) and age; per-square values use the same basis; no value is worded as guaranteed proceeds. | #282, #274, #287 | unit, e2e | Draft |
| UXF-J2-07 | A low-confidence match produces REVIEW with alternatives and a correction path; after correction the decision is recomputed and the correction recorded with time and author. | #282, #273, #291 | unit, e2e | Draft |
| UXF-J2-08 | Opening item details from a decision and returning restores focus to the originating control, **after H-05**: A workspace switch with "Back to"; B side panel with its own address. | #287, #267, #266 | e2e, a11y | Draft |

## J3 Guided stash scan

| ID | Given / when / then | Consumers | Evidence | Status |
| --- | --- | --- | --- | --- |
| UXF-J3-01 | The next-capture instruction names what to do in game terms ("scroll down one screen so row 37 is at the top"), on desktop and tablet, never over the game. | #283, #290 | e2e | Draft |
| UXF-J3-02 | Coverage is expressed as stash area or rows observed, not as screenshots taken. | #283 | unit, e2e | Draft |
| UXF-J3-03 | Overlapping captures merge duplicates; each capture reports stacks seen, duplicates merged and stacks added, and totals equal the sum of additions. | #283, #273 | unit | Draft |
| UXF-J3-04 | Group counts (Keep, Sell, Use soon, Review) total the stacks observed; the review count is broken down by reason; key counts agree between summary and review. | #283, #274 | unit | Draft |
| UXF-J3-05 | Finishing with partial coverage requires confirmation, saves the snapshot as partial, and treats uncovered stash as unknown, never zero. | #283, #270 | unit, e2e | Draft |
| UXF-J3-06 | Observed positions and the manual organisation plan are shown separately; the plan states that the player moves the items and the companion sends no input. | #283 | e2e | Draft |
| UXF-J3-07 | Stash Scan entry point is **after H-06**: A Intel; B Prepare; Capture, Full stash works in both. | #283, #267 | e2e | Draft |

## J4 Next-raid plan

| ID | Given / when / then | Consumers | Evidence | Status |
| --- | --- | --- | --- | --- |
| UXF-J4-01 | Requirements show confirmed and unknown separately, each with its source and time ("seen in stash scan at 18:20, 42 of 68 rows"); an unverified requirement is never shown as ready. | #288, #283, #274 | unit, e2e | Draft |
| UXF-J4-02 | The extract is separate from objectives; objective counts match the list. | #288 | unit | Draft |
| UXF-J4-03 | A route estimate is labelled modelled, shows full model provenance, and explains each trade-off with the data behind it. | #288, #275, #286 | unit, e2e | Draft |
| UXF-J4-04 | Every map has an equivalent text list with the same route order, zones and extracts, reachable by keyboard and screen reader. | #286, #288, #266 | a11y, e2e | Draft |
| UXF-J4-05 | Sharing requires an explicit scope: only me, my paired devices, or team; a role that cannot share to team is refused with the reason and the private plan is unaffected. | #288, #289, #276 | unit, e2e | Draft |
| UXF-J4-06 | When the route model fails, objectives, requirements and waypoints remain usable and a retry is offered. | #288, #275, #268 | e2e | Draft |

## J5 Paired tablet

| ID | Given / when / then | Consumers | Evidence | Status |
| --- | --- | --- | --- | --- |
| UXF-J5-01 | The tablet always shows its mode (Follow desktop, Control desktop, Independent view) and a navigation-owner line. | #290, #276 | e2e | Draft |
| UXF-J5-02 | In Follow desktop, tablet navigation is unavailable with a stated reason, not a silent no-op. | #290, #276 | e2e, a11y | Draft |
| UXF-J5-03 | In Control desktop, a tablet change is applied on the desktop and confirmed with the applied revision; "Show this view on desktop" is absent. | #290, #277, #276 | protocol, e2e | Draft |
| UXF-J5-04 | A tablet command based on a stale revision is rejected, nothing is applied, and the tablet shows what changed first with a choice to reapply. | #276, #277, #290 | protocol, e2e | Draft |
| UXF-J5-05 | In Independent view, browsing never changes the desktop until "Show this view on desktop". | #290, #276 | protocol, e2e | Draft |
| UXF-J5-06 | A mark can be placed without drag by choosing a named place; it advances and records the canonical desktop revision and acknowledgement ID alongside author, device, scope, time and lifetime; the default scope is not team. | #290, #276, #289 | protocol, e2e, a11y | Draft |
| UXF-J5-07 | A paired device is shown everywhere as the player's own device, never as a squad member, and its visible actions match its role. | #290, #278, #289 | protocol, e2e | Draft |

## J6 Debrief and correction

| ID | Given / when / then | Consumers | Evidence | Status |
| --- | --- | --- | --- | --- |
| UXF-J6-01 | Every timeline fact is labelled Observed, Inferred, Estimated or Manual with its source. | #291, #270 | unit, e2e | Draft |
| UXF-J6-02 | Carried value from a capture is labelled an estimate at capture time and never as extracted value. | #291, #282 | unit | Draft |
| UXF-J6-03 | A correction saves with time and author and offers Undo; focus moves to Undo. | #291, #266 | e2e, a11y | Draft |
| UXF-J6-04 | A failed save keeps the correction as a visible draft, moves focus to Retry, and Retry saves it without re-entry. | #291, #270, #268 | e2e, a11y | Draft |
| UXF-J6-05 | The traffic prediction shown at raid time is preserved with its model version and is not re-scored by a newer model. | #291, #275 | unit | Draft |
| UXF-J6-06 | Debrief or History label is **after H-07**. | #291, #267 | e2e | Draft |

## Capture flow

From [capture-intent-mismatch.md](capture-intent-mismatch.md).

| ID | Given / when / then | Consumers | Evidence | Status |
| --- | --- | --- | --- | --- |
| UXF-CAP-01 | A detected context that disagrees with the armed intent is queued as needing a decision, announced politely, shown on every paired device, and does not open a dialog, move focus, or bring the companion window forward. | #271, #264, #267, #276, #287, #290 | unit, e2e, a11y | Draft |
| UXF-CAP-02 | A file still being written is waited for within a bound and is never analysed truncated; reaching the bound yields a decision item, not a silent drop. | #271 | unit, e2e | Draft |
| UXF-CAP-03 | A duplicate file (same content, any name) produces no second result and can be analysed again deliberately. | #271 | unit | Draft |
| UXF-CAP-04 | An unknown context changes no raid, stash or plan state; low confidence yields "couldn't tell", never a mismatch. | #271, #264, #299 | unit, e2e | Draft |
| UXF-CAP-05 | An unknown or mismatched capture stays at the head of the single arrival-ordered queue until analysed or skipped; every later capture waits unread, and a rejected capture does not advance a stash session. | #271, #283 | unit | Draft |
| UXF-CAP-06 | Concurrent intent changes: the stale command is rejected with a visible conflict on its sender, and a screenshot binds to the intent revision in force when the file appeared. | #276, #277, #271 | protocol, unit | Draft |
| UXF-CAP-07 | After analysis, skip, or pause, decoded pixels are released; the screenshot file is byte-for-byte unchanged and still in place; no image is persisted without Debug Capture. | #271, #281, #279, #264 | unit, e2e | Draft |

No fixture here covers the "Flea listings you opened" intent: no journey exercises it, so #284 needs its own fixture rather than one inferred from these sessions.

## Workspace states

From [state-matrix.md](state-matrix.md). One fixture family per workspace; each family has eight
cases (empty, loading, offline, stale, partial, permission denied, failed, success).

| ID | Given / when / then | Consumers | Evidence | Status |
| --- | --- | --- | --- | --- |
| UXF-ST-RAID | Each of the eight Raid states renders distinctly with the matrix's usable remainder, provenance, recovery, focus and announcement, and tablet behaviour. | #286, #267, #268, #276 | e2e, a11y | Draft |
| UXF-ST-INTEL | As above for Intel. | #287, #267, #268 | e2e, a11y | Draft |
| UXF-ST-PLAN | As above for Plan or Prepare. | #288, #267, #268 | e2e, a11y | Draft |
| UXF-ST-TEAM | As above for Team. | #289, #290, #278 | e2e, a11y | Draft |
| UXF-ST-DEBRIEF | As above for Debrief or History. | #291, #270 | e2e, a11y | Draft |
| UXF-ST-SETUP | As above for Setup & Admin and Home readiness. | #292, #281 | e2e, a11y | Draft |
| UXF-ST-RULES | Across all workspaces: background changes preserve rail/header focus and unsubmitted search text; failures of the player's own action move focus to the failure and announce assertively; unknown is never zero; modelled layers are never labelled live. | #266, #267, #264, #279 | unit, e2e, a11y | Draft |

## Accessibility flows

From [accessibility-flows.md](accessibility-flows.md). Evidence must come from the native application;
storyboard results do not satisfy these.

| ID | Given / when / then | Consumers | Evidence | Status |
| --- | --- | --- | --- | --- |
| UXF-A11Y-K | Flows K1 to K4 complete by keyboard only with visible focus at every stop. | #266, #279 | a11y | Draft |
| UXF-A11Y-SR | Flows SR1 to SR5 produce the expected names, roles and announcements with Narrator and NVDA on the native app. | #266, #279 | a11y | Draft |
| UXF-A11Y-T | Flows T1 to T5 on a real tablet: every target at least 44 by 44 px; no drag-only action. | #290, #266 | a11y | Draft |
| UXF-A11Y-R | Flows R1 to R6: no loss of content or function at 200% text scaling and in a narrow docked window; only tables scroll horizontally. | #266, #267, #279 | a11y, e2e | Draft |
| UXF-A11Y-HC | Flows HC1 to HC6 under Windows contrast themes. | #266, #279 | a11y | Draft |
| UXF-A11Y-RM | Flows RM1 to RM3 with animations off. | #266 | a11y | Draft |
| UXF-A11Y-FR | Focus restoration cases FR1 to FR10. | #266, #267 | a11y, e2e | Draft |

## Linking

When this map is revised after sessions, each consuming issue gets one comment listing its fixture IDs
and their status, so the issue body does not have to change for every revision. No such comment has
been posted yet.
