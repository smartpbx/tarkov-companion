# V2 experience validation report (#265)

> ## Status: not yet run
>
> No participant session has taken place. Every table below is intentionally empty. Do not fill a
> cell with an estimate, an example, a persona, or a result from another study. Replace this banner
> only when the first counted session is recorded, and then with "Status: in progress, N counted
> sessions of at least 5". This is a public, aggregate-only report: do not add participant IDs,
> pseudonyms, initials, request codes, evidence-index references, or per-person rows anywhere.

| Field | Value |
| --- | --- |
| Report status | **Not yet run** |
| Sessions held | 0 |
| Counted participants | 0 of at least 5 |
| Coverage met | No ([participant-screening.md](participant-screening.md)) |
| Storyboard revision used | — |
| Moderator(s) | — |
| Synthesiser(s) | — |
| Decision owner sign-off | — |
| Date signed | — |

## 1. Summary

*(Written last. Two paragraphs at most: what was learned about the grouping, the navigation model,
capture recovery, truthfulness and access, each with the counts behind it.)*

## 2. Method as run

Record any departure from [research-plan.md](research-plan.md), with the reason.

| Planned | As run | Reason |
| --- | --- | --- |
| — | — | — |

### Storyboard revisions

| Revision (commit) | Change | Reason | Counted sessions using it |
| --- | --- | --- | --- |
| — | — | — | — |

## 3. Aggregate participant coverage

See [consent-and-data-handling.md](consent-and-data-handling.md). Screener answers and per-person
recruitment, eligibility, scheduling and assignment rows never enter public git, even with
pseudonymous IDs. Calculate this section from the private roster and publish aggregates only. If a
small-cell count would identify someone, write `suppressed; privately verified met/not met` rather
than combining attributes.

| Coverage (from participant-screening.md) | Counted participants meeting it |
| --- | --- |
| Expert | — |
| Regular or returning | — |
| Accessibility-relevant | — |
| Screen-reader user | — |
| Squad player | — |
| Second-screen user | — |
| Not seen v2 concepts | — |
| Helped plan v2 (familiarity flag) | — |

Do not cross-tabulate these rows: the combination can identify someone in a small internal pool.
Prior exposure to v2 or helping plan it is reported only as an aggregate count when the cell is not
identifying; otherwise write `suppressed; privately assessed` and state the aggregate bias effect
without linking it to a finding, a task outcome, or an individual.

## 4. Findability results

One row per task and variant. Counts only. Times listed individually for unaided successes.

| Task | Variant | Attempts | Success | With difficulty | Assisted | Fail | Not attempted | Wrong first clicks (labels) | Unaided times (s) |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| F-01 | A | — | — | — | — | — | — | — | — |
| F-01 | B | — | — | — | — | — | — | — | — |
| *(F-02 to F-12, both variants)* | | | | | | | | | |

## 5. Journey results

| Journey step | Variant | Attempts | Success | With difficulty | Assisted | Fail | Not attempted | Wrong paths | Unaided times (s) |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| J1.1 | A | — | — | — | — | — | — | — | — |
| J1.1 | B | — | — | — | — | — | — | — | — |
| *(every Measure step in [journeys.md](journeys.md), both variants)* | | | | | | | | | |

Ease is asked once per journey, so it has its own table.

| Journey | Variant | Ease responses (each, 1 to 7) | Skipped |
| --- | --- | --- | --- |
| J1 | A | — | — |
| *(J1 to J6, both variants)* | | | |

### Assistive-technology sessions

Report aggregate attempts and barriers separately and never average times with other sessions. Name
a technology or setting only with consent and only when the cell is large enough not to identify a
person; otherwise use a broader category. Participant IDs and per-person technology rows remain in
the private session logs.

| Technology or setting category | Counted sessions | Journey attempts | Aggregate outcomes | Barrier findings |
| --- | --- | --- | --- | --- |
| *(none yet)* | | | | |

## 6. Comprehension and trust probes

Individual answers and participant-level paraphrases remain in private session logs. This public
section may contain only counts and a de-identified **aggregate** paraphrase across responses. The
only exception is an exact quote that separately completed the approval, seven-day embargo and
final revocation check in [consent-and-data-handling.md](consent-and-data-handling.md); it carries no
participant ID or identifying combination of attributes.

| Probe | Journey | Correct | Partly | Incorrect | Not asked | Calibrated | Over-trusts | Under-trusts | De-identified aggregate paraphrase or separately approved exact quote after embargo (no ID) |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| *(each probe in [journeys.md](journeys.md))* | | | | | | | | | Aggregate paraphrase or separately approved exact quote after embargo; never an individual paraphrase |

## 7. Findings

Full records use [templates/finding.md](templates/finding.md); this is the register.

| ID (FND-###) | Title | Codes | Screens | Variant | Count affected (aggregate) | Final severity | Owner issue | Status |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| *(none yet)* | | | | | | | | |

## 8. Variant comparison

Apply the pre-registered rules in [metrics-and-analysis.md](metrics-and-analysis.md). State "no
evidence of a difference" when the thresholds are not met. Preference reasons follow the same
privacy rule as probes: only a de-identified aggregate paraphrase, unless an exact quote separately
completed approval, embargo and the final revocation check.

| Question | Evidence (counts, findings) | Rule outcome | Participant preferences (counts, aggregate reasons) |
| --- | --- | --- | --- |
| Where first launch lands (H-03) | — | — | — |
| Intel as a workspace or a panel (H-05) | — | — | — |
| Stash scan placement (H-06) | — | — | — |
| Plan or Prepare; Debrief or History (H-07) | — | — | — |
| Overall navigation model (H-02) | — | — | — |

## 9. Hypothesis outcomes

Proposed by the synthesiser, decided by the decision owner in [../decision-log.md](../decision-log.md).

| Hypothesis | Proposed outcome | Evidence | Decision-log entry |
| --- | --- | --- | --- |
| H-01 to H-18 | — | — | — |

## 10. Changes to fixtures and the revision brief

| Fixture or brief item | Change | Because of |
| --- | --- | --- |
| — | — | — |

## 11. Completion gate

Each line must point at evidence in this report. See [research-plan.md](research-plan.md) §8.

- [ ] At least five counted participants, coverage met
- [ ] Every gate-controlled **Measure** step in J1 to J6 has at least 2 counted attempts in Variant A and 2 in Variant B; see the explicit 26-step register in [journeys.md](journeys.md). `Not attempted`, `stopped`, `not reached`, and storyboard-defect rows never count
- [ ] F-01 to F-12 each have at least 2 counted attempts in Variant A and 2 in Variant B; `Not attempted` never counts
- [ ] Each variant primary for at least two counted participants
- [ ] Accessibility flows exercised by at least one daily user of the relevant technology or setting
- [ ] Findings coded and rated; every S0 and S1 has an owner issue
- [ ] Every hypothesis accepted, rejected or deferred in the decision log
- [ ] Fixture map revised and linked from implementation issues
