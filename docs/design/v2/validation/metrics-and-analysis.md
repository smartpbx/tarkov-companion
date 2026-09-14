# Metrics and analysis

> **Status: not yet run.** No measurement exists yet. Nothing below is a result.

Five to eight participants find usability problems well. They do not estimate rates. Every rule on
this page follows from that: count what happened, describe it precisely, and never dress a handful
of sessions up as a percentage.

## Per task

Record these for every findability task and every journey step marked **Measure** in
[journeys.md](journeys.md), in [templates/session-log.md](templates/session-log.md).

| Measure | Definition | Values |
| --- | --- | --- |
| **Outcome** | Whether the success criterion in the script was met, and how | `Success` unaided · `Success with difficulty` (recovered from at least one wrong path without help) · `Assisted` (moderator gave any hint) · `Fail` (gave up, wrong end state believed correct, or the time cap passed) · `Not attempted` (skipped, stopped, not reached, or `covered by F-12` where journeys.md skips a step for that reason) |
| **Time on task** | From the moment the participant finishes reading the task card and says "ready" to the moment the success criterion is met, they say they are done, or the cap passes | Seconds, or blank. Cap: 3 minutes for findability, 6 minutes per journey step unless the script says otherwise |
| **First click** | The first navigation control activated (link, button, tab, rail item, search, keyboard shortcut) | The control's label, and `correct` or `wrong` against the expected path for that variant |
| **Wrong paths** | Each time the participant enters a destination not on the expected path and has to come back | Count, and the destinations |
| **Confusion points** | A moment the participant hesitates for more than about 5 seconds, says they are unsure, misreads a label aloud, or asks a question | Timestamp and a short verbatim or paraphrase, coded as below |
| **Ease** | Single Ease Question after each journey: "Overall, how difficult or easy was that?" | 1 very difficult to 7 very easy, or blank if skipped |
| **Comprehension probe** | The probe questions in each journey, asked after the task | `Correct` · `Partly` · `Incorrect` · `Not asked`, plus the answer in the participant's words |
| **Trust calibration** | For probes about live versus modelled, estimate versus proceeds, observed versus inferred | Whether the participant's confidence matched what the screen actually knows: `Calibrated` · `Over-trusts` · `Under-trusts` |

Rules:

- Time is **never** recorded for `Assisted`, `Fail` or `Not attempted` outcomes, and a blank is never
  replaced by an estimate.
- An assistive-technology participant's times are reported separately, not averaged with others:
  they measure a different interaction, not a slower one.
- If a storyboard defect caused the outcome (a broken link, a script error), the task is recorded as
  `Not attempted: storyboard defect` and the defect is fixed and logged before the next session.

## Coding confusion points

Code each confusion point with one primary code. Codes may be added during synthesis if two or more
points need one; record the addition in the report.

| Code | Meaning |
| --- | --- |
| `NAV-PLACE` | Looked for the thing in a different destination |
| `NAV-LABEL` | Did not understand or misread a destination name |
| `NAV-BACK` | Could not get back to where they came from |
| `CAP-FIND` | Could not find how to start or arm a capture |
| `CAP-STATE` | Unsure whether a capture was armed, running, finished, or needed a decision |
| `CAP-MISMATCH` | Misunderstood or mis-resolved an intent or context disagreement |
| `DEC-VOCAB` | Misread TAKE, SWAP, LEAVE, REVIEW, Keep, Sell, Use soon or Review |
| `DEC-MATH` | Numbers did not reconcile for them (space, counts, values) |
| `TRUTH-LIVE` | Read modelled or historical information as live |
| `TRUTH-VALUE` | Read an estimate as a guaranteed value, or gross as net |
| `TRUTH-PROV` | Did not notice or misread source, age, coverage or confidence |
| `TRUTH-SAMPLE` | Treated sample or illustrative content as a real fact |
| `PRIV-FILE` | Believed the companion deletes, moves or keeps their screenshot when it does not |
| `SYNC-MODE` | Misunderstood Follow, Control or Independent, or who leads navigation |
| `SYNC-IDENTITY` | Believed the tablet is a separate squad member |
| `SYNC-CONFLICT` | Misunderstood a revision conflict or what was applied |
| `STATE` | Misread an empty, loading, offline, stale, partial, denied or failed state |
| `A11Y-FOCUS` | Focus lost, moved unexpectedly, or not returned |
| `A11Y-NAME` | A control or region had a missing, wrong or unhelpful name or announcement |
| `A11Y-REFLOW` | Content clipped, overlapped or needed two-dimensional scrolling |
| `A11Y-TARGET` | Target too small or too close for touch or pointer |
| `A11Y-CONTRAST` | Could not see a state, focus indicator or text |
| `OTHER` | None of the above; describe |

## Synthesis

1. **Affinity.** Group confusion points by code, then by the screen or step where they happened.
2. **Findings.** A finding is a problem stated as what happened, where, and why it matters, with
   the participants who showed it. Write each on [templates/finding.md](templates/finding.md) and
   list it in [templates/findings-register.md](templates/findings-register.md).
3. **Severity.** Rate each finding with [severity-rubric.md](severity-rubric.md). Two people rate
   independently; disagreements are discussed and the final rating and reason recorded.
4. **Hypotheses.** For each hypothesis in [research-plan.md](research-plan.md), list the findings and
   counts that bear on it, then propose accepted, rejected or deferred. The decision owner decides.

Participant-level coding and the participant-to-evidence index stay in the private study folder.
The public report receives task/variant counts and de-identified synthesis only: never screener
answers, IDs, eligibility flags, assignments, schedules or cross-tabs that identify a person.

## Comparing the two variants honestly

With two or three primary participants per variant, a difference of one person is noise. The
comparison uses **pre-registered decision rules**, written here before any session so that results
cannot shape the rules afterwards.

With five counted participants a task or step gets three attempts in one variant and two in the
other, so raw counts would penalise the variant that happened to be tried more. Each rule below
therefore compares **equal numbers of attempts**: when the counts differ, only the earliest
attempts by participant order in the larger group are compared, as many as the smaller group has.
The remaining attempts are reported alongside, but do not move a rule.

A variant **loses a placement question** (for example, where Stash scan lives) when, for the
relevant findability task or journey step, comparing equal numbers of attempts:

- at least two more of its attempts had a wrong first click or wrong path than the other
  variant's, **and**
- at least one of those produced an S1 or S2 finding.

A task whose card accepts every destination in one variant cannot produce a wrong first click
there, so it cannot move a placement rule. F-11 is such a task (every A destination is correct);
it is reported descriptively only.

A variant is **disqualified for a journey** when it has any S0 finding, or an S1 finding seen by two
or more participants, that the other variant does not have.

Otherwise the result is **no evidence of a difference**, and the decision owner chooses on product
grounds, recorded as such. Participant preference is reported, with reasons, but never overrides an
S0 or S1 finding.

A mixed outcome is allowed and expected: for example, keeping A's persistent destinations but
adopting B's contextual Intel. The decision log records each part separately.

## What the report may and may not say

Wording patterns only; the angle-bracket values are placeholders, not results.

| May say | May not say |
| --- | --- |
| "<n> of <N> participants opened Intel first when asked to find stash scan in Variant A" | "<x>% of users look in Intel" |
| "Median time for the <n> unaided successes was <t> s" (only with every one of those times listed) | An average that leaves out a recorded unaided success, or includes an assisted one |
| "<n> of <N> participants read the traffic label as live" (with each probe answer quoted or summarised) | "Users understand the traffic is modelled" |
| "Variant B had <k> fewer wrong paths in J2; below the pre-registered threshold" | "Variant B is better" |
| "Not tested with a screen-reader user; recruitment gap recorded" | "Accessible" |
