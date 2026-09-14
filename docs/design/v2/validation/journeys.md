# Moderated journeys

> **Status: not yet run.** These are scripts with expected answers. The expected answers are what
> the sample content says, so the moderator can score a probe; they are not observed results.

Six journeys, run in the participant's primary variant (research-plan.md §4). Before each journey:
open **Moderator controls**, choose **Reset session**, then the journey's start point. Read the
**task card** aloud and hand it over in writing, including in large print or as text for a screen
reader if the participant wants it. Do not name a destination or control that is not on the card.

- **Measure** marks a step that gets an outcome, time, first click and wrong paths
  ([metrics-and-analysis.md](metrics-and-analysis.md)).
- **Inject** marks a moderator action from the panel. Do it exactly when the script says.
- **Probe** questions are asked after the journey, not during it, unless marked otherwise.
- Always say at the start: "Every name and number here is sample data. If something looks different
  from the game, tell us, but treat it as made up."

Fixture IDs refer to [acceptance-fixture-map.md](acceptance-fixture-map.md).

## Completion-gate measurement register

The completion gate counts these **26** and no other journey rows. Each one needs at least two
counted attempts in Variant A and two in Variant B; an outcome of `Not attempted`, `stopped`, `not
reached`, or `storyboard defect` contributes zero. The attempt is bounded by the time cap in
[metrics-and-analysis.md](metrics-and-analysis.md): six minutes per row unless that document changes
the cap before a session. An injection or probe without **Measure** is observed and recorded but is
not a gate-controlled attempt.

| Journey | Required measured steps |
| --- | --- |
| J1 | J1.1, J1.2, J1.3 |
| J2 | J2.1, J2.2, J2.3, J2.4, J2.5, J2.6 |
| J3 | J3.0, J3.1, J3.4, J3.5, J3.6 |
| J4 | J4.1, J4.2, J4.3, J4.4 |
| J5 | J5.1, J5.2, J5.3, J5.4 |
| J6 | J6.1, J6.2, J6.3, J6.4 |

The moderator cannot close #265 from a journey-level label such as “J3 attempted”: each listed
row must have its own bounded attempt count. A skipped J2.5 remains zero even when F-12 was done;
F-12 supplies its own findability evidence, not a substitute journey-step attempt.

---

## J1 First launch

**Start:** A `variant-a.html#/setup` · B `variant-b.html#/home`. Profile not chosen.

**Task card:** "You've just installed Tarkov Companion and opened it for the first time. The game
isn't running. Get it ready to give you advice about your own quests, and find out what it does with
the screenshots you take."

| # | Step | Expected path A | Expected path B | Success criterion |
| --- | --- | --- | --- | --- |
| 1 | **Measure.** Find what still needs doing | Lands on Setup & Admin; reads Get ready: "1 item needs action"; Profile and wipe "Not chosen" | Lands on Home; same Get ready content | Participant names Profile and wipe as the only thing needing action |
| 2 | **Measure.** Choose a profile | Choose profile, then Sample profile PvP, then Use this profile | Same | Status reads "Chosen"; header reads "Setup: nothing needs action" |
| 3 | **Measure.** Find what happens to screenshots | Privacy at a glance, What happens to screenshots | Same | Participant opens the detail and reads or paraphrases the capture-analysis line |

**Probes and expected answers**

1. "Is the companion ready for everything now? How do you know?" Every check shows its own status, and a
   check time where a check ran; Team and tablet is optional and off; game data synced 12 minutes ago. *(H-15)*
2. "What happens to a screenshot you take in the game?" The file stays in the EFT folder; the
   decoded image is discarded after analysis; folder cleanup is off. *(H-14)*
3. "If you close this and come back tomorrow, where would you go to change the screenshot folder?"
   A: Setup & Admin. B: the header Setup link or Home. *(H-03)*
4. "Is anything on this screen real data about you?" No, sample data. *(`TRUTH-SAMPLE`)*

**Watch for:** reading a green "Found" as "everything is ready"; looking for a gear icon; in A,
treating Setup & Admin as a settings dump rather than a starting point.

**Fixtures:** UXF-J1-01 to UXF-J1-04, UXF-CAP-07.

---

## J2 Loot decision: TAKE, SWAP, LEAVE

**Start:** `#/raid` in either variant. Nothing armed.

**Task card:** "You're in a Customs raid with a loot container open and your backpack visible.
Before you take the screenshot, set the companion up for a loot decision. When the result arrives,
decide what you'd take."

| # | Step | Expected path A | Expected path B | Success criterion |
| --- | --- | --- | --- | --- |
| 1 | **Measure.** Arm a loot decision | Header Capture, Loot decision, Arm (or Alt+Shift+C) | Same | Header reads "Armed: Loot decision" |
| 2 | **Inject** "Detected context disagrees", then read card 2 aloud: *"You pressed the key while your stash was still open by mistake."* **Measure** noticing and resolving it | Header: "1 capture needs a decision", Decide, dialog "This looks like a stash, not a loot screen" | Same | Notices the decision without a prompt, then chooses **Skip this screenshot** (or Analyse as Full stash and says why). **Incorrect:** Analyse as Loot decision anyway. Record whether the moderator had to point at the header |
| 3 | **Inject** "Matches what is armed". **Measure** finding the result | Stage progress, then "Capture N ready", Open result; or Raid, Latest capture | Same | Loot decision page open |
| 4 | **Measure.** Say what you'd take and what, if anything, you'd drop | Reads the decisions table | Same | Takes fuel conditioner, Virtex processor and bolts; takes the electric drill and drops Wires; leaves the Crickent lighter; says the military cable needs checking |
| 5 | **Measure.** Check the flea fee for the electric drill, then return. *Skip and record "covered by F-12" if this participant did F-12 in this variant* | Details for Electric drill, Intel workspace, reads Estimated fee ₽6,000, Back to Loot decision | Details, Intel panel beside the decision, reads ₽6,000, Close Intel | Reports ₽6,000 and is back on the loot decision |
| 6 | **Measure.** Fix the uncertain match: it is a power cord | Correct match, Power cord, Save correction | Same | Row reads Power cord, LEAVE |

**Arithmetic the participant can check** (all sample values, in
[prototype/content.js](prototype/content.js)):

| Fact | Value |
| --- | --- |
| Container | 6 × 4 = 24 squares; 6 items use 2 + 1 + 1 + 2 + 1 + 2 = 9 |
| Backpack | 4 × 4 = 16 squares; 12 used, 4 free as one 2 × 2 block |
| TAKE | Fuel conditioner 1 × 2, Virtex 1 × 1, bolts 1 × 1: 4 squares, leaving 0 free |
| SWAP | Electric drill 2 × 1 into the Wires slot 2 × 1 |
| Swap gain | ₽58,000 − ₽24,000 = ₽34,000, flea net estimate |
| Decisions | TAKE 3 + SWAP 1 + LEAVE 1 + REVIEW 1 = 6 of 6 container items |
| Electric drill | Gross ₽64,000 − estimated fee ₽6,000 = ₽58,000 net est.; 2 squares, ₽29,000 per square |

**Probes and expected answers**

1. "What does SWAP ask you to do?" Drop Wires from the backpack and take the electric drill.
   *(H-08)*
2. "How much space will you have left if you follow all of it?" None: 16 of 16. *(`DEC-MATH`)*
3. "If you sell the drill on the flea market, will you get ₽58,000?" Not guaranteed. It is a sample
   price, 12 minutes old, less an estimated fee; the gross is ₽64,000. *(H-09)*
4. "Why is bolts TAKE when it's below your value band?" The hideout still needs 6. *(H-08)*
5. "Did the companion move anything in your inventory?" No. Advice only, no input to the game.
   *(S0 check)*
6. "What happened to the accidental screenshot?" Skipped by you; nothing changed; it is still listed
   under recent captures. *(H-13)*
7. "What happened to the screenshot file of the loot?" It stays in the EFT folder; the decoded image
   was discarded. *(H-14)*

**Fixtures:** UXF-J2-01 to UXF-J2-08, UXF-CAP-01, UXF-CAP-07.

---

## J3 Guided stash scan

**Start:** `#/raid` in either variant. Two screenshots are already analysed after the participant
finds Stash scan. This makes the first step a required measured route in every session rather than a
conditional, uncounted substitute for F-06.

**Task card:** "You're partway through scanning your whole stash. Two screenshots are done. Carry on,
then find out what needs checking before you sell anything."

| # | Step | Expected (both variants) | Success criterion |
| --- | --- | --- | --- |
| 0 | **Measure.** Find Stash scan | A: Intel, Open stash scan · B: Prepare, Open stash scan · either variant: Capture, Full stash | Stash scan is open; record the actual route. |
| 1 | **Measure.** Say what to do next and how much is covered | Reads Next and Coverage | "Scroll down one screen so row 37 is at the top"; 42 of 68 rows |
| 2 | Participant arms Full stash if not armed. **Inject** "File still being written" | Progress says the file is still being written and will not be skipped, then completes | Participant does not take the screenshot again or conclude it failed. Record what they say while it waits |
| 3 | **Inject** "Duplicate of the last file". Ask (probe, now): "Did anything change?" | Announcement: already analysed; recent captures shows "Same file as capture N" | "No" |
| 4 | **Inject** "Context unknown", then read card 2: *"That one was your stash, but the companion couldn't tell."* **Measure** | Header: "1 capture needs a decision", Decide, dialog "Couldn't tell what capture N shows" | Nothing is pre-selected. Chooses Full stash and Analyse as chosen, or Skip and retake. **Incorrect:** Loot decision |
| 5 | **Measure.** How many stacks need review, and why? | Groups panel after capture 3 | 26: 3 keys, 8 unreadable stack counts, 15 uncertain identities |
| 6 | **Measure.** Stop here and keep what you have | Finish with partial coverage, Save partial snapshot | Saved; participant says rows not covered stay unknown |

**Counts the participant can check:** capture 1 rows 1 to 24, 46 stacks · capture 2 rows 19 to 42,
45 stacks with 7 duplicates merged, 38 added · 84 = Keep 20 + Sell 31 + Use soon 14 + Review 19 ·
capture 3 rows 37 to 60, 38 stacks with 5 duplicates, 33 added · 117 = Keep 27 + Sell 45 + Use soon
19 + Review 26 · coverage 60 of 68 rows. The storyboard does not add rows for the step 4 capture;
the moderator says so if asked.

**Probes and expected answers**

1. "Will the companion sort your stash for you?" No. The organisation plan is for you to do by hand.
   *(S0 check)*
2. "Are the items shown where they are now, or where they should go?" Where they were seen; nothing
   is rearranged. *(render-audit RA-S2)*
3. "What does the companion think is in rows 61 to 68?" Unknown, not zero. *(`TRUTH-PROV`)*
4. "Is ₽1,910,000 what you'll get for the Sell group?" A flea net estimate, and 3 of 45 stacks are not
   priced. *(H-09)*
5. "What happened to the stash screenshots on your computer?" Files remain; decoded images were
   discarded. *(H-14)*

**Fixtures:** UXF-J3-01 to UXF-J3-07, UXF-CAP-02, UXF-CAP-03, UXF-CAP-04, UXF-CAP-05, UXF-CAP-07.

---

## J4 Next-raid plan

**Start:** A `#/plan` · B `#/prepare`.

**Task card:** "You're planning your next Customs raid. Decide whether you're ready to go and how
you'd approach it, then share the plan with your own tablet only."

| # | Step | Expected (A Plan · B Prepare) | Success criterion |
| --- | --- | --- | --- |
| 1 | **Measure.** Are you ready? | Requirements: 2 confirmed · 1 unknown | Names food and water as not checked |
| 2 | **Measure.** Why does the route go through Dorms, and how long will it take? | Route estimate panel | Objective 3 is in Dorms; 24 to 29 min, modelled |
| 3 | **Measure.** "Show me the route order without the map." | List toggle | Reads Big Red, Construction, Dorms, ZB-1011 |
| 4 | **Measure.** Share with your own tablet only | Share, My paired devices, Share | Announcement: shared with My paired devices |
| 5 | **Inject** Workspace state "Failed". Ask: "Can you still use this plan?" | Failed banner: route model failed | Objectives, requirements and waypoints still work; Retry route |

**Probes and expected answers**

1. "Does the hatched area around Dorms show where players are right now?" No. Modelled from a
   historical sample dataset: data through 2026-09-12, generated 2026-09-13 04:00 UTC, 1,284 raids,
   6 of 9 zones covered, confidence medium, model traffic-sample-v0. *(H-10; `TRUTH-LIVE` is S0)*
2. "How does the companion know you have the Dorm room 214 key?" Seen in a stash scan at 18:20 that
   covered 42 of 68 rows. *(H-17 pattern)*
3. "Who can see the plan now?" Only your paired devices. *(scope)*
4. "Where would you find the Plan page tomorrow?" A: Plan · B: Prepare, or Home, Continue. *(H-07)*

**Fixtures:** UXF-J4-01 to UXF-J4-06.

---

## J5 Paired tablet: control and marking

**Start:** `#/tablet` in either variant, tablet mode Follow desktop. Use a real tablet for touch
participants (see [prototype/index.html](prototype/index.html) for how to open it there).

**Task card:** "You're at your desk with your tablet beside you. From the tablet, make the desktop
companion show your plan. Then leave a waypoint at Warehouse 4 that only your own devices can see.
Finally, look at something on the tablet without changing the desktop."

| # | Step | Expected | Success criterion |
| --- | --- | --- | --- |
| 1 | **Measure.** Make the tablet able to change the desktop | Tablet mode: Control desktop | Owner line reads "This tablet leads" |
| 2 | **Inject** "Desktop user switches view" *before* the participant taps a destination. **Measure** | A: tap Plan · B: tap Prepare. Dialog: "Desktop changed first" | Participant explains the desktop changed first and nothing was applied, then makes the desktop end on A: Plan or B: Prepare. If they choose Keep desktop view first, record that choice and require one unaided retry to the named destination before scoring success. |
| 3 | **Measure.** Waypoint at Warehouse 4, own devices only | Waypoint, Place Warehouse 4, Share with: My paired devices, Add | Mark listed with "shared with My paired devices". **Incorrect:** Only me or Team |
| 4 | **Measure.** Browse on the tablet without changing the desktop, then send it to the desktop | Independent view, tap Team, Show this view on desktop | Desktop panel shows Team with a new revision |

**Probes and expected answers**

1. "Right now, who is leading navigation?" Read from the owner line. *(H-11)*
2. "Is your tablet a member of your squad? Can Birch see it?" No: it is your own paired device and
   not a squad member. *(H-12; `SYNC-IDENTITY` floor S1)*
3. "Can the tablet move your character or press keys in the game?" No: companion state only.
   *(S0 check)*
4. "When the desktop changed first, what happened to your tap?" Nothing was applied until you chose.
   *(`SYNC-CONFLICT`)*
5. "How long does a ping last?" 45 seconds, never saved. *(marks lifetime)*

**Fixtures:** UXF-J5-01 to UXF-J5-07. (UXF-CAP-06, the intent race, is exercised only by the moderator's "Tablet changes intent at the same time" injection outside the journeys.)

---

## J6 Debrief and correction

**Start:** A `#/debrief` · B `#/history`, opened from the moderator's J6 link, which switches the header to after the raid (local time 18:56, last raid ended 18:52:30).

**Task card:** "Your Customs raid just ended. Check what the companion knows about it, fix what it
got wrong, and tell it whether the traffic prediction matched what you saw."

| # | Step | Expected (A Debrief · B History) | Success criterion |
| --- | --- | --- | --- |
| 1 | **Measure.** "How does the companion know you extracted at ZB-1011?" | Timeline row "Extracted at ZB-1011", Inferred | Inferred from last position and raid end; not confirmed by the game |
| 2 | **Inject** "Next correction save fails" before the participant starts this step. **Measure** correcting the military cable to a power cord | Correct match, Power cord, Save correction; Failed banner and draft line appear, focus on Retry save; Retry save | Correction saved after retry; participant did not re-enter it |
| 3 | **Measure.** Undo it | Undo correction | Back to "Could be Power cord" with Correct match available |
| 4 | **Measure.** Say the traffic was about as shown | "Compared with what you saw": About as shown | Announcement: Recorded |

**Probes and expected answers**

1. "Did you extract with ₽258,000 of loot?" Unknown. That was an estimate at 18:41 from a capture,
   not extracted value. *(H-17; `TRUTH-VALUE`)*
2. "Is this traffic prediction the latest model?" It is the prediction shown at raid time,
   preserved, not re-scored. *(H-10)*
3. "Who said you survived?" You entered it. *(H-17)*
4. "What happened to your correction when saving failed?" It was kept as a draft and saved on retry.
   *(data-loss floor)*

**Fixtures:** UXF-J6-01 to UXF-J6-06.

---

## After all journeys: comparison (research-plan.md part 3)

1. Choose the two journeys with the most wrong paths or confusion for this participant, or J2 and J4.
2. Open the other variant at the same start point and walk the expected path yourself while the
   participant watches. Do not ask them to perform it.
3. Ask: "Which of the two would you rather use for this, and why?" and "Is there anything from the
   other one you'd want in this one?"
4. Record preference and reasons verbatim in the session log. Preference is reported, never scored
   as task evidence.
