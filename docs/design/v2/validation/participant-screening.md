# Participant screening

> **Status: not yet run.** Nobody has been screened or recruited. The recruitment log at the end is
> empty on purpose. Never add a row for a person who has not agreed to take part.

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
| Counted participants | 5 | Recruitment log |
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
answers in [templates/screener-response.md](templates/screener-response.md), never in this file.

Screening happens before the consent script, so read or send this notice first:

> These questions help us pick a mix of players for a study of early Tarkov Companion storyboards.
> You can skip any of them. Your answers are kept in a private study folder that only the study
> team can open, never in the project's public repository, and are deleted when the study report
> is signed off, or sooner if you ask.

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

- Assign primary variant by recruitment order after screening (research-plan.md section 4), then
  adjust only to satisfy "each variant as primary: 2 each". Record any adjustment.
- An accessibility-relevant participant chooses their own device, assistive technology and
  settings. The moderator checks the storyboard opens with that setup before the session, not
  during it.
- Send the storyboard folder before a remote session and ask the participant to open
  `prototype/index.html` once, so a blocked local file is found before the session.

## Recruitment log

Pseudonymous IDs only. No names, gamer tags, email addresses or contact details here: this
repository is public. The mapping from ID to person lives only in the private study folder
described in [consent-and-data-handling.md](consent-and-data-handling.md).

| ID | Screened (date) | Counted? | Expert | Regular/returning | Accessibility-relevant | Squad | Second screen | Seen v2 concepts | Primary variant | Session date | Status |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| *(no participants yet)* | | | | | | | | | | | |

### Coverage check

Fill in only from the log above.

| Coverage | Minimum | Recruited | Met? |
| --- | --- | --- | --- |
| Counted participants | 5 | — | — |
| Expert | 1 | — | — |
| Regular or returning | 1 | — | — |
| Accessibility-relevant | 1 | — | — |
| Screen-reader user (if recruitable) | 1 | — | — |
| Squad player | 2 | — | — |
| Second-screen user | 1 | — | — |
| Not seen v2 concepts | 2 | — | — |
| Variant A primary | 2 | — | — |
| Variant B primary | 2 | — | — |
