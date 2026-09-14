# Consent and data handling

> **Status: not yet run.** No consent has been collected and no session data exists.

This is an internal study, but the repository it lives in is **public**. The single most important
rule follows from that: **raw session material never enters this repository.** Only de-identified,
synthesised findings do, and a quote only with the participant's permission.

## What is collected

| Material | Collected? | Where it lives | Retention |
| --- | --- | --- | --- |
| Screener answers | Yes | Private study folder | Until the report is signed off, then deleted |
| ID-to-person mapping | Yes | Private study folder, separate file | Deleted with the raw recordings and notes, so a withdrawal after sign-off can still be honoured |
| Consent record | Yes | Private study folder ([template](templates/consent-record.md)) | Kept until raw material is deleted, then reduced to "consent given, date, ID" |
| Screen recording | Only if agreed | Private study folder | Deleted 30 days after the report is signed off |
| Audio recording | Only if agreed | Private study folder | Deleted 30 days after the report is signed off |
| Webcam video | **No** | — | — |
| Live notes and session logs | Yes | Private study folder | Deleted 30 days after the report is signed off |
| De-identified findings and counts | Yes | This repository, in [validation-report.md](validation-report.md) | Permanent |
| Verbatim quotes | Only if agreed per quote, and not committed until 7 days after that agreement | This repository, attributed to an ID | Permanent, including in the repository's public history |
| Assistive technology used | Only if the participant agrees | Session log, then the report as "screen reader (NVDA)" or similar | Permanent in the de-identified report |

The 30-day retention is this plan's proposal. The decision owner may shorten it; lengthening it
needs a recorded reason in [../decision-log.md](../decision-log.md).

Every limit above is counted from sign-off, and sign-off can be a long way off if recruitment falls
short. So there is also an **outer limit**: each item is deleted at its limit above or 180 days after
that participant's session, whichever comes first. The 180 days is this plan's
proposal (M-06) and is the decision owner's call. If the outer limit is reached before sign-off, the
synthesis already written is kept and the raw material is deleted anyway.

**The private study folder** is a location with access restricted to the moderator, note-taker and
synthesiser, outside this repository and outside any public release feed. The report names the kind
of location, never a path or share link.

## What is never collected

- EFT account names, profile IDs, gamer tags, squad members' names, or friends lists.
- Group keys, relay addresses, pairing codes, TarkovTracker tokens, or any credential.
- The participant's own EFT screenshots, logs or stash contents. Sessions use sample content only.
  If a participant offers a real screenshot, the moderator declines it.
- Health information. A participant's access need is recorded only as the technology or setting they
  use, in their own words, and only if they agree. The reason is never asked.

## Consent script

Read this before any recording starts. Adapt the wording, never the substance.

> Thanks for helping. We are testing early storyboards of a redesign of Tarkov Companion, not
> testing you. There are no wrong answers, and if something is confusing, that is exactly what we
> need to find.
>
> Everything you will see uses made-up sample data. The storyboard does not connect to the game,
> does not read your files, and does not send anything over the network.
>
> We would like to [record the screen and audio / record audio / take notes only]. Recordings and
> notes stay in a private folder that only the study team can open, and are deleted 30 days after
> the report is signed off, and never more than 180 days after today. The report is public inside the project's repository, so it refers to you
> only as a participant number, and it will only quote you word for word if you say that is okay for that quote. Once something
> is in the repository, earlier versions stay visible in its public history, so we wait 7 days after
> you agree to a quote before adding it, in case you change your mind.
>
> If you use assistive technology or particular display settings, you can tell us what you use so
> we can understand what we see, but you do not have to, and we will not ask why.
>
> You can skip any task or question, take a break, or stop at any time. You can also ask us
> afterwards to remove your data; if the report is not yet signed off, we remove everything from you,
> and if it is, we delete your raw material and remove anything attributed to your number from the
> next revision of the report. That revision cannot erase what earlier public versions showed, which
> is why quotes wait 7 days and nothing else in the report is attributed to you.
>
> Do you have any questions? Are you happy to go ahead, and are you happy with [the recording
> choice]?

Record the answers in [templates/consent-record.md](templates/consent-record.md). Recording starts
only after a yes to both questions.

## During the session

- If the participant starts sharing their own screen, ask them to close anything personal (game
  launcher, chat, email) before sharing.
- If personal information appears anyway, note the timestamp and cut it from the recording before
  synthesis.
- A participant who becomes frustrated is offered a break or to skip. The task is logged as "not
  attempted" or "stopped", never as a failure the participant has to explain.

## Withdrawal

1. The participant contacts the moderator by any means.
2. Within 7 days the study team deletes that participant's recordings, notes, screener answers and
   mapping.
3. If the report is unsigned, their findings are removed and counts recalculated. If signed, the
   next report revision removes quotes and per-participant rows for that ID and states that a
   participant withdrew. Aggregated counts are recalculated and the decision log notes any decision
   whose evidence changed.
4. A withdrawn participant no longer counts toward the five, so the completion gate may reopen.

## Before findings are committed

The synthesiser checks every change to [validation-report.md](validation-report.md) for:

- no names, tags, contact details, paths, share links or credentials;
- quotes only where the consent record says yes for that quote;
- no detail that identifies a person in a small internal group, for example "the only person on
  the team who uses a screen reader" next to their play schedule. If a finding cannot be written
  without identifying someone, it is summarised at a level that does not.
