# Participant screening

> **Status: not yet run.** Nobody has been screened or recruited. Screener answers, contact details,
> per-person eligibility, assignments and recruitment status belong only in the private study
> folder. Never add them to this public repository, even under a pseudonymous ID.

Before Q1, record a fixed **screening deletion deadline** of screening timestamp + 30 × 24 hours in
UTC. If the candidate is not recruited, delete their screener, contact, roster entry and
request-code verifier together at that deadline; it is not tied to a session, because none exists.

## Who counts

A counted participant is a **real person who plays Escape from Tarkov** and is one of the internal
users the companion is built for (#256). #265 calls these "representative internal users"; this
package calls them counted participants. The five-person minimum in #265 counts only such people. These do **not**
count toward the five:

- the pilot participant;
- anyone who wrote the storyboards, this plan, or the concept prompts, or who moderates or takes
  notes for the study;
- the decision owner, because they will act on the findings;
- simulated users, personas, or AI-generated sessions of any kind.

People excluded from counting may still pilot, observe, or give design feedback. That feedback is
logged separately from participant findings.

## Required coverage

Coverage is a minimum across the counted group, not one person per row. One participant can satisfy
more than one row (an expert who uses a tablet counts for both).

| Coverage | Minimum | How it is confirmed |
| --- | --- | --- |
| Counted participants | 5 | Aggregate count calculated from the private recruitment roster |
| **Expert** workflow: several wipes, plays for progression or profit, has opinions about loot value per square and quest routing | 1 | Screener Q1 to Q4 |
| **Regular or returning** player: plays most weeks, or is coming back after a wipe or break | 1 | Screener Q1 to Q3 |
| **Accessibility-relevant**: uses a screen reader, screen magnification, 200% or larger text, keyboard-only or switch access, voice control, Windows high contrast or a contrast theme, reduced motion by need, or uses a colour filter, a pointer or touch accommodation, or another setting they choose to describe | 1 | Screener Q9, self-described and optional |
| Screen-reader user (subset of the row above) | 1 | Screener Q9. If none can be recruited, the gate stays open; the decision owner may record the gap as a deferral in the decision log, which documents it but does not meet the gate (research-plan.md section 8) |
| Squad player who shares a plan or marks with others | 2 | Screener Q5, "with a squad, sharing plans or marks" |
| Uses a tablet or phone as a second screen while playing, or would | 1 | Screener Q6 |
| Has **not** seen the v2 concept renders | 2 | Screener Q8 |
| Each variant as primary | 2 each | Assigned at scheduling |

## Screener

Keep it short, and ask the accessibility question neutrally. Every question may be skipped. Record
answers only in a private copy of [templates/screener-response.md](templates/screener-response.md).
A filled screener, its filename and any row derived from one are private per-person recruitment
data and must never be committed to git.

Screening happens before the consent script, so read or send this notice first:

> These questions help us pick a mix of players for a study of early Tarkov Companion storyboards.
> You can skip any of them. Before the first question we will give you a private request code and a
> study contact. Your answers are kept in a private study folder that only the study team can open,
> never in the project's public repository. If you are not recruited, we delete the answers,
> contact, recruitment row and request-code verifier at the exact UTC deadline of this screening
> timestamp plus 30 days; if you take part, the study retention rules apply. You can use the code to ask
> for deletion sooner.

1. Roughly how long have you played Escape from Tarkov? *(under 3 months · 3 to 12 months · 1 to 3
   years · more than 3 years)*
2. How many wipes have you played through with real progression? *(none · 1 · 2 to 3 · 4 or more)*
3. How often do you play at the moment? *(most days · most weeks · occasionally · returning after a
   break)*
4. Which of these do you actively think about in a raid? *(choose any: loot value per square ·
   quest items and routing · hideout items · routes and extracts · none of these)*
5. How do you usually play? *(choose any: mostly solo · mostly with a regular squad · with a squad,
   sharing plans or marks)*
6. Do you use a second screen, tablet or phone while playing? What for?
7. Which companion tools do you use now, if any? *(for example tarkov.dev, TarkovTracker, a
   screenshot scanner, Tarkov Companion v1, others)*
8. Have you seen the Tarkov Companion v2 concept images or taken part in planning v2? *(no · seen
   some · helped plan)*
9. **Optional.** Do you use any assistive technology or display setting on your computer, tablet or
   phone? For example a screen reader, magnifier, larger text, keyboard-only, voice control, high
   contrast, reduced motion, or anything else. You only need to say what you use, not why.
10. Which devices could you use for a session? *(Windows PC · other computer · tablet · phone)*
11. Are you comfortable with the session being recorded? *(screen and audio · audio only · notes
    only)*
12. What times suit you?

## Scoring the screener

- **Expert:** Q1 at least "1 to 3 years", Q2 "2 to 3" or more, and Q4 includes loot value per
  square or quest items and routing.
- **Regular or returning:** Q3 "most days", "most weeks" or "returning after a break".
- **Accessibility-relevant:** any use described in Q9. Record the technology or setting only, in the
  participant's own words, and only if they agree.
- **Squad player:** Q5 includes "with a squad, sharing plans or marks".
- A participant who answered Q8 "helped plan" may take part but is flagged on every finding their
  familiarity could affect.

## Scheduling rules

- Keep the private recruitment roster, eligibility calculation, contact details, request-code
  verifier and scheduling assignment outside git. Give the candidate the request code and private
  study contact before asking Q1; store only a salted verifier, never the code itself. For an
  unrecruited candidate, delete all four at the recorded screening deletion deadline rather than
  awaiting a session outcome.
- Before asking a scheduled recruit for consent, record the scheduled-session start and the exact
  consented no-show withdrawal deadline (scheduled start + 7 × 24 hours) on the private screener and
  consent records. Once consent is recorded, retain the contact route and request-code verifier
  through that exact deadline even if the earlier screening deadline passes. If a study task begins,
  replace that no-show lifecycle with the session-start + 180 × 24 hour participant lifecycle in
  [consent-and-data-handling.md](consent-and-data-handling.md).
- Assign primary variant, findability sets and journey rotation from the repeating schedule in
  research-plan.md section 4. Record adjustments only in the private roster.
- An accessibility-relevant participant chooses their own device, assistive technology and
  settings. The moderator checks the storyboard opens with that setup before the session, not
  during it.
- Send the storyboard folder before a remote session and ask the participant to open
  `prototype/index.html` once, so a blocked local file is found before the session.

## Public aggregate coverage

The private roster has one row per candidate. This public file has none: not dates, IDs,
eligibility flags, assignments, status or a link/path to the roster. After recruitment, publish only
the aggregate counts below. Suppress or combine a count if, in this small internal pool, it would
identify someone; a suppressed count does not meet the completion gate until the decision owner can
verify it privately and the report records only `met` or `not met`.

| Coverage | Minimum | Counted completed sessions | Met? |
| --- | --- | --- | --- |
| Counted participants | 5 | 0 | No |
| Expert | 1 | — | — |
| Regular or returning | 1 | — | — |
| Accessibility-relevant | 1 | — | — |
| Screen-reader user | 1 | — | — |
| Squad player | 2 | — | — |
| Second-screen user | 1 | — | — |
| Not seen v2 concepts | 2 | — | — |
| Variant A primary | 2 | — | — |
| Variant B primary | 2 | — | — |

These aggregates are recalculated after a withdrawal. Screening and scheduling totals are never
published as a per-person table and never substitute for completed counted sessions.
