# Research plan

> **Status: not yet run.** No session has been scheduled or held. Dates, participants and results
> below are blank on purpose.

Issue: #265. Parent: #256. Decision owner: the product owner named on #256. Storyboards:
[prototype/](prototype/).

## 1. Why this study exists

#256 proposes replacing fourteen v1 pages with six destinations: **Raid, Intel, Plan, Team,
Debrief** and **Setup & Admin**, and making a user-started screenshot the primary way to ask the
companion a question. #256 also says the workspace names "are a design direction, not sacred
labels." Once #267 builds the adaptive shell, moving a destination means changing navigation
contracts, deep links, tablet sync state and fixtures in several issues at once. This study is the
cheap point to find out whether the grouping and vocabulary hold up.

## 2. Research questions

1. **Grouping.** When players think about a task, do they look for it where #256 put it?
2. **Navigation model.** Does a persistent workspace rail (Variant A) or a workflow hub with
   contextual Intel (Variant B) lead to fewer wrong turns and less assistance across the six
   journeys?
3. **Capture-first.** Can players arm, take, and act on a capture from anywhere, and recover when
   what they armed and what the companion detected disagree?
4. **Truthfulness.** Do players read modelled traffic, estimated prices, partial coverage, stale
   data and inferred events as exactly what they are?
5. **Paired tablet.** Do players understand Follow, Control and Independent modes, who is leading
   navigation, and that the tablet is their own device rather than a squad member?
6. **Access.** Can keyboard, screen-reader, magnification, high-contrast and touch users complete
   the same journeys, and where do they get stuck?

## 3. Hypotheses

Each hypothesis has an ID used in [../decision-log.md](../decision-log.md) and
[validation-report.md](validation-report.md). All start **Open**.

| ID | Hypothesis | Main evidence |
| --- | --- | --- |
| H-01 | Players place tasks in Raid, Intel, Plan, Team, Debrief and Setup & Admin the way #256 intends. | Findability first clicks in Variant A, which carries the #256 grouping. Variant B's results are reported as the alternative grouping, not as evidence for H-01 |
| H-02 | One navigation model (A rail or B hub) produces fewer wrong first clicks and fewer assisted completions across the six journeys. | Journeys J1 to J6, findability tasks |
| H-03 | A distinct Home (B) is used for first launch and "where was I"; A's labelled Setup & Admin as the first-launch landing is not mistaken for a settings dump. | J1, which starts at each variant's own first-launch landing. F-01 and F-11 start from Raid, so they are descriptive only and are not evidence for the landing |
| H-04 | A Capture action in the header on every page is found from any workspace without prompting. | J2, J3, findability F-05 |
| H-05 | Intel opened beside the current page, at its own address (B), keeps context better than switching to an Intel workspace (A), without hiding Intel from people who start from an item. | J2 details step, F-02, F-12 |
| H-06 | Stash scan is found under Intel (A) or under Prepare (B). | J3 start, F-06 |
| H-07 | "Debrief" and "Plan" (A) or "History" and "Prepare" (B) are understood without explanation. | J4, J6, F-08, F-09 |
| H-08 | TAKE, SWAP, LEAVE and REVIEW are read correctly, including which carried item SWAP drops. | J2 probes |
| H-09 | "Flea net est." with gross and fee on the details is read as an estimate, not guaranteed proceeds. | J2 and J3 probes |
| H-10 | The modelled-traffic label and its provenance are read as historical and modelled, never live. | J4 probes, Raid findability F-04 |
| H-11 | Follow desktop, Control desktop and Independent view, the navigation-owner line, and the conflict dialog are understood. | J5 |
| H-12 | The paired tablet is understood as the player's own device, not a squad member. | J5 probe |
| H-13 | The mismatch dialog is resolved without analysing the wrong thing, and participants understand nothing changed until they chose. | J2 injected mismatch, J3 injected cases |
| H-14 | The storyboard's privacy line, "Capture analysis: the decoded image is discarded after analysis. The screenshot file stays in your EFT folder.", is understood as written. | J1 privacy probe, J2 probe |
| H-15 | A counted status ("1 setup item needs action") is read correctly: participants can say what still needs action and do not read the page as fully ready. The storyboards have no single "Ready" pill, so this does not compare the two; the concept renders' pill is an expert-review issue (render-audit.md), not participant evidence. | J1 step 1 and its probe, F-01 |
| H-16 | A finished capture that announces itself and offers "Open result" is noticed and opened without prompting, and participants keep their place. The storyboards have no self-navigating condition, so this does not compare the two. | J2, J3 |
| H-17 | Observed, Inferred, Manual and Estimated labels in Debrief are understood and trusted appropriately. | J6 probes |
| H-18 | The List alternative to every map is discoverable and enough to choose a route. | J4, accessibility sessions |

## 4. Method

**Moderated usability sessions with think-aloud**, in person or by screen share, one participant at
a time. Remote sessions use the participant's own computer, browser, assistive technology and
display settings; the storyboard folder is sent to them as files, because it needs no server.

### Design

- **Primary variant, alternating.** Counted assignment slots alternate primary variant (P01 A, P02
  B, P03 A, P04 B, then repeat that four-slot cycle). Each participant runs all six journeys in
  their primary variant.
- **Findability tasks on both variants.** The twelve findability tasks in
  [navigation-variants.md](navigation-variants.md) are split into two matched sets. Each
  participant does set 1 on one variant and set 2 on the other. Which set goes on Variant A follows
  the table below, so primary variant and findability set are crossed rather than tied together.
  This shows each variant every task without asking anyone to find the same thing twice.
  | Participant | Primary variant | Set on A | Set on B |
  | --- | --- | --- | --- |
  | P01 | A | 1 | 2 |
  | P02 | B | 1 | 2 |
  | P03 | A | 2 | 1 |
  | P04 | B | 2 | 1 |
  | P05 | A | 1 | 2 |
  | P06 | B | 1 | 2 |
  | P07 | A | 2 | 1 |
  | P08 | B | 2 | 1 |
  | P09 onward | Repeat P01 to P08 in order | | |

  The P-number here is a private assignment slot, not a public participant identifier. P09 repeats
  P01, P10 repeats P02, and so on (`((slot - 1) mod 8) + 1`). If somebody is found ineligible,
  withdraws, or does not complete enough of the session to count, the replacement recruit takes the
  same vacated slot and therefore the same primary variant and findability sets. If a withdrawal
  happens after synthesis, recruit against that removed slot before taking the next new slot. The
  private roster records the replacement; public files contain aggregate attempts only.
- **Journey order.** J1 first launch always comes first and J6 Debrief always last, because the
  story needs them there. J2 to J5 rotate by participant (P01 2-3-4-5, P02 3-4-5-2, P03 4-5-2-3,
  P04 5-2-3-4), then repeat every four assignment slots (P05 as P01, P06 as P02, and so on).
  A replacement uses the vacated slot's rotation rather than the next rotation.
- **Injected edge cases.** Each journey has fixed moderator injections (a mismatch in J2; still
  writing, duplicate and unknown in J3; a desktop-first conflict in J5; a failed save in J6), so
  every participant meets the same recovery situations.
- **Comparison.** At the end, the moderator shows the other variant's path for two journeys the
  participant struggled with most, or J2 and J4 if none, and asks for a preference with reasons.

### Session outline (85 to 95 minutes)

| Part | Minutes | Content |
| --- | --- | --- |
| 0 | 5 | Consent ([consent-and-data-handling.md](consent-and-data-handling.md)), recording choice, background questions |
| 1 | 20 | Findability: set 1 on one variant, set 2 on the other (twelve tasks; most take well under the 3-minute cap) |
| 2 | 45 to 55 | Journeys J1 to J6 in the primary variant, each followed by a single ease question and probes |
| 3 | 10 | Comparison walkthrough and preference |
| 4 | 5 | Wrap-up, anything the participant wants to add, withdrawal reminder |

Accessibility-relevant participants may split this into two sessions of about 60 minutes each and
use [templates/accessibility-session-addendum.md](templates/accessibility-session-addendum.md). A
break is offered after part 2 to everyone.

### Pilot

One pilot session runs before P01 with someone who knows the project, and it **does not count**
toward the five. Its purpose is to catch storyboard defects and unclear task wording. Every change
made after the pilot is logged in [validation-report.md](validation-report.md) with the storyboard
commit, so the report can say which revision each participant saw.

## 5. Roles

| Role | Responsibility |
| --- | --- |
| Moderator | Runs the script, injects edge cases, never teaches the interface during a task |
| Note-taker | Fills [templates/session-log.md](templates/session-log.md) live, timestamps confusion points |
| Observer (optional) | Silent. Questions go to the moderator in writing, after the task |
| Synthesiser | Codes findings with [metrics-and-analysis.md](metrics-and-analysis.md) and [severity-rubric.md](severity-rubric.md) |
| Decision owner | Moves hypotheses in [../decision-log.md](../decision-log.md), signs off the report |

Where one person must hold several roles, the report says so. The person who wrote the storyboards
should not be the only synthesiser.

## 6. Materials

- Storyboards: [prototype/variant-a.html](prototype/variant-a.html) and
  [prototype/variant-b.html](prototype/variant-b.html), both reading
  [prototype/content.js](prototype/content.js), so both show identical sample content.
- Scripts and expected paths: [journeys.md](journeys.md) and
  [navigation-variants.md](navigation-variants.md).
- Accessibility protocol: [accessibility-flows.md](accessibility-flows.md).
- Blank evidence: [templates/](templates/).

## 7. Known limitations and biases

- **Internal pool.** Participants may know v1, the concept renders, or the people building v2. The
  screener records prior exposure, and the report states it next to every finding it could affect.
- **Storyboards are not the product.** They cannot show recognition errors, real latency, or
  native assistive-technology behaviour. A pass here is not accessibility evidence for #266.
- **Sample content.** Experts may notice that sizes or prices differ from the game. Moderators say
  up front that every number is sample content, and note when an expert's reaction is to the sample
  value rather than the design.
- **Think-aloud slows people down.** Times are diagnostic, never benchmarks. They are not
  comparable with the #256 "under three minutes" first-launch target, which applies to the native
  build.
- **Five people find problems, not rates.** The analysis reports counts ("2 of 5"), never
  percentages or significance, and a variant difference of one participant is not treated as a
  preference.

## 8. Completion gate

#265's validation criteria are met only when **all** of these are true, and the report shows the
evidence for each:

1. At least five counted participants took part, meeting every coverage requirement in
   [participant-screening.md](participant-screening.md). The pilot is not one of them.
2. Every required journey J1 to J6 has at least **two counted attempts in Variant A and two counted
   attempts in Variant B**. `Not attempted`, `stopped`, `not reached`, a storyboard defect and a
   documented reason are honest records but contribute zero attempts; recruit a replacement or run
   an additional counted session until each minimum is met. A missing attempt is never filled in or
   estimated.
3. Every findability task F-01 to F-12 has at least **two counted attempts in each variant**. The
   same zero-credit rule applies to `Not attempted`, including a journey step skipped as `covered by
   F-12`; the F-12 task itself must still meet this minimum.
4. Each variant was the primary variant for at least two counted participants.
5. Accessibility flows were exercised by at least one counted participant who uses, in daily life,
   at least one of the technologies or settings in [accessibility-flows.md](accessibility-flows.md),
   not by a sighted mouse user simulating it. Flows with no such participant are listed in the
   report as not exercised.
6. Findings are coded and rated, and every S0 and S1 finding has an owner issue.
7. Every hypothesis in section 3 is accepted, rejected or deferred in
   [../decision-log.md](../decision-log.md) with the evidence that moved it.
8. [acceptance-fixture-map.md](acceptance-fixture-map.md) is revised from those decisions and
   linked from the implementation issues.

If internal recruitment cannot meet a coverage requirement, the gate stays open. The decision
owner may record a deliberate deferral in the decision log, naming the gap and its risk; a sighted
simulation is not a substitute for it. A recorded deferral documents the gap honestly. It does not
by itself meet the gate: that needs the #256 decision owner to amend #265's acceptance criteria
on the issue.
