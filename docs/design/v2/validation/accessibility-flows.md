# Accessibility flows

> **Status: not yet run.** No participant has used these flows. The only checks so far are automated
> checks of the storyboard files themselves (listed at the end), which prove the storyboards are
> usable enough to test with, and nothing about the native application.

These flows are the #265 "annotated keyboard, screen-reader, touch, narrow-window, high-contrast, and
high-text-scale flows". Each is written as the expected sequence, so a moderator or participant can
say where reality departed from it. They are run inside the six journeys, not as a separate test,
by participants who use the technology or setting in daily life ([participant-screening.md](participant-screening.md)).

**What the storyboards can and cannot show.** They are HTML. Keyboard order, focus visibility,
reflow, contrast, touch target size and motion are genuinely testable in them. Screen-reader
behaviour is only indicative: NVDA, JAWS or VoiceOver in a browser reads ARIA roles, while the
native companion will expose UI Automation to Narrator and NVDA through Avalonia. Every "SR" line
below is a vocabulary and sequencing expectation for #266 to implement natively, not a claim that
the native app will behave this way.

## Shared expectations

| Aspect | Expectation |
| --- | --- |
| Landmarks | Banner (header with context and Capture), navigation "Workspaces", main, complementary "Moderator controls" (storyboard only). Variant B adds a search landmark in the header. |
| Headings | One h1 per page naming the destination; h2 per panel; dialog titles are h2. |
| Names | Every control's name is its visible text. Repeated controls add hidden context: "Details for Electric drill", "Correct match for Military cable". |
| Current location | Rail or hub link for the current destination has `aria-current="page"` and a visible non-colour marker (arrow and border). |
| Status | One polite status region for background changes and confirmations; one assertive region only for refusals, failures of the player's own action, and conflicts. |
| Focus | Page change: focus to the h1. Dialog open: focus to the dialog heading. Dialog close: focus back to the control that opened it, or to the h1 if that control no longer exists. Re-render: focus kept on the same control. Background events: focus never moves. |
| Shortcuts | Alt+Shift+C opens Capture. It can be switched off (WCAG 2.1.4). No single-character shortcuts. |
| Colour | No state is conveyed by colour alone: decisions are words (TAKE, SWAP, LEAVE, REVIEW) with distinct border styles; statuses have a symbol and a word; traffic zones have a hatch density and a text level. |
| Targets | At least 44 by 44 CSS px for every control, 48 px high for tablet mode and mark buttons. |
| Motion | No motion carries information. With reduced motion, stage progress still updates but without transitions, and faster. |

Native translation notes for #266: h1 and landmarks map to UIA `LandmarkType` and heading levels;
`aria-current` to selection state on the navigation item; polite and assertive regions to UIA
`LiveSetting` Polite and Assertive on a status element; dialog heading focus to initial focus on the
dialog title with `AutomationProperties.Name` on the window; hidden context text to
`AutomationProperties.Name` or `HelpText`.

---

## K. Keyboard only

**Setup:** mouse and touchpad put aside. Browser at 100%. Any operating system.

### K1. Arm a capture from anywhere (J2 step 1)

| # | Keys | Focus lands on | Expect |
| --- | --- | --- | --- |
| 1 | Tab from page load | "Skip to content" link, visible | Skip link is the first stop |
| 2 | Tab | Header items in reading order: setup status link, (B) Setup, (B) Search field and button, Capture | Capture is reachable within the header without entering the rail |
| 3 | Enter on Capture, **or** Alt+Shift+C from anywhere | Dialog heading "Capture" | Focus visibly on the heading |
| 4 | Tab | First intent radio | Arrow keys move between intents; Space selects |
| 5 | Tab to Arm, Enter | Capture button in the header | Header text: "Armed: Loot decision (rev N, set on desktop)"; polite announcement |

**Pass:** no step needs a mouse; focus is visible at every stop; focus returns to Capture.

### K2. Resolve a capture that needs a decision (J2 step 2, J3 step 4)

| # | Keys | Focus lands on | Expect |
| --- | --- | --- | --- |
| 1 | *(moderator injects a disagreement)* | Unchanged | Header shows "1 capture needs a decision" and a Decide button; focus did not move |
| 2 | Shift+Tab or Tab to Decide, Enter | Dialog heading | Title names detected and armed context |
| 3 | Escape | Decide | Capture still pending |
| 4 | Enter on Decide, Tab to "Skip this screenshot", Enter | The page heading or the next control that still exists | Polite: "Capture N skipped. Nothing changed." |

**Pass:** Enter on the heading does nothing; Escape never discards; focus never lands on the page body.

### K3. Decide what to take, check a price, and come back (J2 steps 4 and 5)

| # | Keys | Focus lands on | Expect |
| --- | --- | --- | --- |
| 1 | Tab into the decisions table | "What would change this" disclosure, then Details, then Correct match (REVIEW row) | Row order matches the visual order; the table caption is "Decisions, strongest reason first" |
| 2 | Enter on "Details for Electric drill" | **A:** Intel page h1 · **B:** Intel panel heading "Electric drill" | B keeps the loot decision visible beside the panel |
| 3 | **A:** Tab to "Back to Loot decision", Enter · **B:** Tab to "Close Intel", Enter | The Details link for Electric drill | Returns to the same row, not the top of the page |

### K4. Tablet mode and marks without a pointer (J5)

| # | Keys | Focus lands on | Expect |
| --- | --- | --- | --- |
| 1 | Tab to "Tablet mode" group, arrow to Control desktop | Control desktop radio | Owner line changes; polite announcement of the owner line; focus stays on the radio |
| 2 | Tab to a destination button, Enter | Button | In Follow mode the button is announced as unavailable with the reason; in Control mode it applies |
| 3 | Enter on Waypoint | Dialog heading "Add waypoint" | Place is a select, scope is a radio group; no drag on the map needed |
| 4 | Choose Warehouse 4, My paired devices, Add | Waypoint button | Mark listed; polite: "Waypoint added at Warehouse 4, shared with My paired devices." |

---

## SR. Screen reader

**Setup:** the participant's own screen reader and browser (for example NVDA with Firefox or Chrome,
JAWS with Chrome, VoiceOver with Safari). Speech rate and verbosity as they normally use them.
Moderator does not describe the screen.

### SR1. Orientation on first launch (J1)

| # | Action | Expected speech, in substance |
| --- | --- | --- |
| 1 | Load the page | Page title "Setup & Admin · Storyboard A" or "Home · Storyboard B" |
| 2 | Landmarks list | Banner, Workspaces navigation, main, Moderator controls |
| 3 | Headings list | h1 Setup & Admin or Home; h2 Get ready; h3 per check; h2 Privacy at a glance |
| 4 | Read Get ready | "1 item needs action." Each check: name, status word (Found, Available, Synced 12 min ago, Not chosen, Off (optional)), detail |
| 5 | Activate "Choose profile for Profile and wipe" | Dialog "Choose profile and wipe", then the radio group "Profile" |
| 6 | Use this profile | Back on the button, now "Change profile for Profile and wipe"; status: "Profile chosen. Nothing in setup needs action." |

**Watch:** status symbols (✓ ! ○ ?) should not be read as noise before the word; if they are, that
is an `A11Y-NAME` finding for the native design, where symbols must be decorative.

### SR2. Hearing a capture arrive and deciding (J2)

| # | Event or action | Expected speech |
| --- | --- | --- |
| 1 | Screenshot simulated (disagreement) | Polite: "Capture N needs a decision: detected Full stash but Loot decision was armed. Nothing was changed." Current reading position is not interrupted mid-word and not moved |
| 2 | Navigate to the header, Decide | "Decide, button" with description "Armed: Loot decision … 1 capture needs a decision" |
| 3 | Open | "This looks like a stash, not a loot screen, dialog. Nothing has been changed yet. Choose how to analyse capture N." |
| 4 | Screenshot simulated (match) | Polite: "Screenshot seen. Analysing as Loot decision." then, once, "Capture N result ready: Loot decision." Stage-by-stage changes are **not** announced |

### SR3. Reading a decision and its numbers (J2 step 4)

| # | Action | Expected speech |
| --- | --- | --- |
| 1 | Table navigation to the Electric drill row | Row header "Electric drill"; column headers read with cells: Decision SWAP; Why, an ordered list of three reasons, then "Swap out Wires … Gain ₽34,000 flea net est."; Size 2×1; Flea net est. ₽58,000; Per square ₽29,000; Match confidence 0.93 |
| 2 | Summary heading | "6 of 6 container items: TAKE 3 · SWAP 1 · LEAVE 1 · REVIEW 1" |

**Watch:** whether "est." and "×" are read intelligibly; whether ₽ is read as "rouble" or skipped.
Either is a vocabulary finding for #266 and #274, not a storyboard defect.

### SR4. Conflict on the tablet (J5 step 2)

| # | Event or action | Expected speech |
| --- | --- | --- |
| 1 | Desktop changes view (moderator) | Polite only: "Desktop changed to Plan, Woods." Focus does not move and no dialog opens until the player acts |
| 2 | Activate Prepare or Plan | Assertive: "Change not applied: desktop changed first." Dialog "Desktop changed first" with its description |
| 3 | "Show Prepare on desktop" | Polite: "Desktop now shows Prepare. Confirmed at rev N." |

### SR5. Failed save and draft (J6 step 2)

| # | Event or action | Expected speech |
| --- | --- | --- |
| 1 | Save correction (moderator set the next save to fail) | Assertive: "Correction not saved. Your choice is kept as a draft. Retry save." Focus on "Retry save" |
| 2 | Retry save | Polite: "Saved: corrected to Power cord. Undo is available." Focus on "Undo correction" |

---

## T. Touch

**Setup:** a real tablet (or a phone for the narrow flow), the participant's usual grip and posture.
Open the storyboard folder on the device, `#/tablet`.

| # | Flow | Expect |
| --- | --- | --- |
| T1 | Choose a tablet mode (J5 step 1) | The whole label is the target, at least 48 px high; the selected mode is shown by border and weight, not colour alone |
| T2 | Tap a destination in Follow mode | The button looks unavailable (dashed border) and a note explains why; no silent no-op |
| T3 | Place a waypoint (J5 step 3) | No drag or precise map tap required; dialog controls reachable with one hand in portrait and landscape |
| T4 | Decide on a capture from the tablet | The tablet's own Capture area shows "needs a decision" and Decide; the dialog fits without horizontal scrolling |
| T5 | Mark buttons, undo and overflow | Every control at least 44 by 44 px with at least 8 px between neighbours. Record any mis-tap as `A11Y-TARGET` |

The concept render's small tablet controls are recorded as RA-T6 in [render-audit.md](render-audit.md).

---

## R. Narrow phone, tablet, desktop, 200% text and 400% zoom

| Case | Setup | Expect |
| --- | --- | --- |
| R1 Desktop | 1280 to 1920 px wide window | Two-column layouts (map beside panels); rail on the left |
| R2 Tablet | 768 to 1024 px | Rail may still be beside content at the upper end; panels wrap to fewer columns |
| R3 Narrow phone | 320 to 400 px | Navigation becomes a wrapping row above content; one column; tables scroll horizontally inside their own region; nothing else scrolls horizontally |
| R4 400% zoom | Browser zoom 400% on a 1280 px window (320 CSS px) | Same as R3 (WCAG 1.4.10). Dialogs fit the viewport and scroll vertically |
| R5 200% text | Browser text-only zoom or OS text scaling at 200%, 1280 px window | No clipped or overlapping text; labels wrap; buttons grow; no loss of content or function (WCAG 1.4.4) |
| R6 Narrow window beside the game | Desktop window resized to about 480 px while "in raid" | Loot decision is readable without horizontal page scroll; the decisions table scrolls within itself and keeps row headers readable |

**Pass:** the participant can complete J2 and J5 at their normal zoom or text size without panning
in two dimensions except inside a table.

---

## HC. High contrast and forced colours

**Setup:** Windows Contrast themes (Aquatic, Desert, Dusk, Night sky) with the participant's usual
browser, or the participant's own contrast setting on another OS. Moderator's "High contrast theme"
toggle only if neither is available, and recorded as such.

| # | Check | Expect |
| --- | --- | --- |
| HC1 | Current destination | Still distinguishable without colour (arrow marker, system highlight border) |
| HC2 | Focus indicator | Visible on every control, using the system highlight colour |
| HC3 | Decisions | TAKE, SWAP, LEAVE, REVIEW readable and distinguishable by word and border style |
| HC4 | Map | Zone outlines, route line, extracts and "You" drawn in system text colour; levels readable as text; the List view carries everything |
| HC5 | Status and modelled label | "Modelled traffic · not live" keeps its border and remains readable |
| HC6 | Pressed and selected states | Map or List toggle and tablet mode show selection with the system highlight |

---

## RM. Reduced motion

**Setup:** operating-system "reduce motion" or "show animations: off"; moderator toggle as fallback.

| # | Check | Expect |
| --- | --- | --- |
| RM1 | Page and panel changes | No transitions |
| RM2 | Capture stage progress | Still shows each stage and the final result; no animated spinner; nothing depends on watching movement |
| RM3 | Ping lifetime | Expiry is stated in text ("Expires in 45 s"); no pulsing |

---

## ML. Map and list alternative

| # | Flow | Expect |
| --- | --- | --- |
| ML1 | Raid: choose a route using only the List view (J4-style task on Raid) | Zones with levels, extracts with status, and the selected route order are all present as text; route choice radios are outside the map |
| ML2 | Plan or Prepare: route order without the map (J4 step 3) | "Big Red, Construction, Dorms, ZB-1011" as an ordered list |
| ML3 | The map image itself | Announced as an image named "Customs schematic with … route" and described as schematic with a pointer to the List view |
| ML4 | Map or List toggle state | Announced as a pressed toggle button; the choice is kept when moving between Raid and Plan |

---

## FR. Focus restoration

Run during any journey; the moderator notes every place focus went somewhere unexpected.

| # | Situation | Focus must go to |
| --- | --- | --- |
| FR1 | Navigate to a destination | The page h1 |
| FR2 | Open any dialog | The dialog heading |
| FR3 | Close a dialog by Escape or Cancel | The control that opened it |
| FR4 | Close a dialog whose opener no longer exists (for example the last pending decision resolved) | The page h1 |
| FR5 | Toggle Map or List, change route, change tablet mode | The same control after the page updates |
| FR6 | Open Intel beside a page (B) | The Intel panel heading |
| FR7 | Close Intel (B) or go Back (A) | The Details link that opened it |
| FR8 | Background event: sync, stale, capture arriving, state change | Unchanged |
| FR9 | Failure of the player's own save | The Retry control |
| FR10 | Successful correction | Undo |

---

## Storyboard self-checks already run

These were run once against the committed storyboard files with a headless browser, with network
access observed rather than assumed. They are **not participant evidence**, and they say nothing
about the native application. They are listed so a reviewer can repeat them.

- Both variants load from `file://` and make **no request other than `file://`**, before and after
  every flow below.
- No console error on load, across all 48 workspace-state renders per variant, or after the flows.
- One h1 per page, required landmarks and both live regions present, every button, link, input and
  select has an accessible name, and no `aria-labelledby` or `aria-describedby` points at a missing
  id, on every route of both variants.
- Alt+Shift+C opens Capture with focus on the heading; Escape returns focus to Capture.
- A disagreement is queued without opening a dialog or moving focus; Decide opens the dialog on its
  heading; Escape keeps the capture pending and returns focus to Decide; Skip resolves it.
- Unknown context resolved as a chosen context runs to a result; still-writing completes; duplicate
  is announced; an intent race opens the conflict dialog with an assertive announcement.
- Tablet Control mode keeps focus on the radio; a desktop-first change raises the conflict dialog;
  applying it confirms a new revision.
- A correction saves with focus on Undo; a failed save keeps a visible draft with focus on Retry, and
  Retry saves it.
- No horizontal page scroll outside table regions at 320 CSS px with normal text, and at 768 and
  1280 px with 200% text, on nine routes per variant.
- Every tablet-preview control is at least 44 by 44 CSS px at 1024 px wide.
