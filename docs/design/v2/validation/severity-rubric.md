# Severity rubric

> **Status: not yet run.** No finding has been rated.

Severity describes the **consequence for a player** if the problem shipped, adjusted by how many
participants met it and whether they could recover. Two raters score independently, then agree a
final rating and write down the reason.

## Levels

| Level | Name | A finding is this level when | Required response |
| --- | --- | --- | --- |
| **S0** | Boundary or truth breach | A participant believes, from what the design shows, that the companion does something outside the fixed boundary (reads game memory, controls or plays the game, sorts the stash itself, draws over the game, tracks enemies live) **or** acts, or says they would act, on information the design misrepresents as more certain than it is: modelled traffic read as live positions, an estimate read as guaranteed proceeds that changes a decision, an inferred event read as observed, sample content read as real, or a belief that analysis deletes or keeps their screenshot when it does not. **One participant is enough.** | Blocks the affected design direction. Owner issue before any implementation of that surface. Recorded in the decision log. |
| **S1** | Critical | A participant cannot complete a journey step without help and there is no workaround they found; **or** a participant loses work, analyses the wrong capture without realising, or applies an unintended change to another device; **or** an assistive-technology user is blocked (focus lost with no way back, an unlabelled required control, an announcement that never comes for a blocking state). | Must be fixed in the revision brief and in the owning implementation issue's acceptance criteria. |
| **S2** | Major | A participant completes the step only with significant difficulty (a wrong path and a recovery, or close to the step's time cap; passing the cap is a `Fail`, see
metrics-and-analysis.md), or makes a recoverable wrong decision; **or** two or more participants misread the same label; **or** an assistive-technology user completes the step only with a workaround. | Should be fixed before the owning issue closes; deferral needs a decision-log entry. |
| **S3** | Minor | Hesitation or a misreading that the participant corrected alone, with no effect on the outcome. | Fix when the surface is touched; track in the findings register. |
| **S4** | Cosmetic or suggestion | Preference, polish, or an idea with no observed problem behind it. | Log for the design system or backlog. No commitment. |

## Floors and adjustments

Apply these after choosing the base level. A floor can raise a rating; nothing lowers one below its
floor.

1. **Truthfulness floor.** Codes `TRUTH-LIVE`, `TRUTH-VALUE`, `TRUTH-SAMPLE`, `PRIV-FILE` and
   `SYNC-IDENTITY` are never below **S1**. They are **S0** when the participant acted on the
   misreading or says they would have, the same test S0 uses above.
2. **Boundary floor.** Any belief that the companion sends input to the game, moves inventory, reads
   game memory, or draws over the game is **S0**, even if the participant laughs it off.
3. **Accessibility floor.** A barrier that stops an assistive-technology user completing a step is
   never below **S1**, regardless of how many participants met it, because the group rarely
   contains more than one such user.
4. **Data-loss floor.** A capture skipped, dropped or overwritten without the participant choosing it,
   or a correction lost, is never below **S1**.
5. **Frequency.** Seen by every counted participant who attempted the step, and by at least two:
   raise by one level, to a maximum of S1 (S0 is reserved for boundary and truth). This uses
   attempts rather than a head count because a step in one variant usually has only two or three.
6. **Recovery.** If every participant who met a problem recovered unaided within a few seconds and
   said so, it may be lowered by one level, never below a floor above.
7. **Storyboard artefacts.** A problem caused only by the storyboard medium (a browser quirk, sample
   data being unlike the game, a moderator control) is rated **S4** and tagged `storyboard`, and
   the underlying design question is logged separately if there is one. Reacting to a sample value
   as unrealistic ("that drill is never worth that") is this rule. Believing sample content is real
   data about the player's own stash or raid is `TRUTH-SAMPLE` under rule 1, and this rule never
   lowers it.

## Recording the rating

Each finding in [templates/finding.md](templates/finding.md) records: base level, floors applied,
adjustments applied, final level, both raters' initial levels, and one sentence of reasoning. A
finding without a reason is not rated.

## Examples of how the rules apply

These illustrate the rules. They are not findings and nothing like them has been observed. Every
quotation below is invented to show a rule and must never be cited as something a participant said.

- *Hypothetical:* a participant says "so it shows me where people are right now" while looking at the modelled
  traffic layer, then picks the route to avoid them: `TRUTH-LIVE`, would act on it, **S0**.
- *Hypothetical:* a screen-reader participant cannot tell that a capture needs a decision because the tray change
  is not announced: `A11Y-NAME` plus `CAP-STATE`, blocks the step, accessibility floor, **S1**.
- *Hypothetical:* two participants read "Debrief" as a quest name, then find it after a wrong path: `NAV-LABEL`,
  difficulty for two, **S2**.
