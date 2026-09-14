# Consent and data handling

> **Status: not yet run.** No consent has been collected and no session data exists.

This is an internal study, but this repository is **public**. Raw material, filled templates,
screener answers, individual probe answers and every per-person recruitment or scheduling record
stay outside it. Public git contains only aggregate coverage and de-identified **aggregate**
paraphrase. The only participant-level words it may contain are an exact quote after that quote's
separate approval, seven-day embargo and final revocation check.

## What is collected

| Material | Collected? | Where it lives | Retention |
| --- | --- | --- | --- |
| Screener answers | Yes | Private study folder | Non-recruits: fixed screening timestamp + 30 × 24 hours. Consented no-shows: fixed no-show deadline. Participants whose session begins: with raw material, below |
| Private recruitment roster | Yes | Private study folder | A non-recruited candidate's entire roster entry is deleted at their fixed screening timestamp + 30 × 24 hour deadline. For a recruited participant, eligibility and assignment fields are deleted when the session is complete. A consented no-show keeps only the scheduling/lifecycle fields needed to execute withdrawal until the fixed no-show deadline below |
| ID/contact/request-code index | Yes | Private study folder, separate access-restricted file | Non-recruits: fixed screening timestamp + 30 × 24 hours. Consented no-shows: fixed scheduled-session start + 7 × 24 hours, even if the screening deadline is earlier. Participants whose session begins: fixed withdrawal deadline, session start + 180 × 24 hours. An on-time withdrawal case retains only the minimum locator/evidence references for no more than 7 additional days to complete deletion |
| Participant-to-evidence index | Yes | Private study folder; IDs mapped to report rows/findings, with no raw answers | Deleted with the ID/contact/request-code index; it exists solely to execute withdrawal and recalculate aggregates |
| Consent and quote-approval record | Yes | Private study folder ([template](templates/consent-record.md)) | Deleted with the ID/contact/request-code index after a deletion receipt is recorded without identity; for a consented no-show that is the fixed no-show deadline |
| Screen recording | Only if agreed | Private study folder | Earlier of 30 days after report sign-off or session start + 180 days |
| Audio recording | Only if agreed | Private study folder | Earlier of 30 days after report sign-off or session start + 180 days |
| Webcam video | **No** | — | — |
| Live notes and session logs | Yes | Private study folder | Earlier of 30 days after report sign-off or session start + 180 days |
| De-identified findings and counts | Yes | This repository, in [validation-report.md](validation-report.md) | Permanent |
| Verbatim quotes | Only if agreed per quote and the embargo below completes | This repository, without a participant ID | Permanent in public history; a current report can be revised but old commits cannot be erased |
| Assistive technology used | Only if the participant agrees and an aggregate cell will not identify them | Private session log, then only an aggregate category in the report | Permanent only as non-identifying aggregate synthesis |

Before Q1, calculate each candidate's **screening deletion deadline** as the screening timestamp
plus 30 × 24 hours, record the exact UTC timestamp beside their private contact, roster and salted
request-code verifier, and tell them that all three are deleted then if they are not recruited.
This deadline exists independently of a session and does not move because scheduling is delayed or
never happens.

When a recruited candidate is scheduled, record the scheduled-session start in UTC. **Before asking
for consent**, calculate the consented no-show withdrawal deadline as that scheduled start + 7 × 24
hours, record both exact timestamps on the screener and consent records, and read the exact deadline
and private request route to the candidate. Once consent is recorded, the contact and salted
request-code verifier stay usable through that exact no-show deadline; the earlier screening
deadline no longer applies. If no study task begins, delete the contact, verifier, consent record and
remaining scheduling/lifecycle row at the no-show deadline, not before it. A reschedule can establish
a new deadline only when it is agreed and recorded **before** fresh consent; it never silently moves
a deadline already accepted.

When the first study task begins, record its session-start timestamp and calculate the participant's
fixed withdrawal deadline as that timestamp + 180 × 24 hours. Record and give the exact UTC value
before collecting the task response. That deadline does not move when sign-off moves. Raw material
can disappear earlier, but the minimal ID/contact/request-code and participant-to-evidence indexes
stay until the deadline so an on-time request can still locate and remove that person's
contribution. At the deadline, two study-team members verify deletion of both indexes and record only
the date, number of records deleted and verifier roles—no ID, contact or code.

After that deadline, the study no longer accepts a new withdrawal request. If a request arrived on
time, the study seals a minimum private case record (verified request time, locator and evidence
references) and may retain it only until deletion completes, no later than seven days after the
deadline; the normal indexes are otherwise deleted at the deadline. After that bounded completion
window, the study no longer has the identity link needed to locate one person's
aggregate contribution, and public git history is immutable. The study therefore cannot promise or
honour a later participant-specific removal. This limit is stated before consent, not discovered
when somebody asks. Retention is proposed in M-06; shortening the request window would require new
consent, and it must never be lengthened for somebody who already consented.

**The private study folder** is access-restricted to the moderator, note-taker and synthesiser,
outside this repository and any public release feed. Its manifest records each item, owner, session,
deletion date and actual deletion time. The ID/contact/request-code index is a separate file with narrower
access. Public files name only the kind of storage, never its path or share link.

## Request code and quote embargo

Before Q1, give the candidate a random request code and the private study contact. The person keeps
the code; the study stores only a salted verifier linked to the private ID. For somebody not
recruited, it works until their recorded screening deletion deadline (screening timestamp + 30 × 24
hours), when the private contact, roster entry and verifier are deleted together. If recruited, the
same code is recorded as delivered on the consent record. It works through the disclosed no-show
deadline if no study task begins; once a session begins, it works for withdrawal and quote
revocation through the fixed participant deadline. A lost code can be resolved through the verified
private contact only while the separate ID/contact map still exists.

Every proposed quote gets its own approval request containing the exact text, approval timestamp,
eligible-to-commit timestamp and the fixed withdrawal deadline, all in UTC. The eligible timestamp
is approval + 7 × 24 hours. The synthesiser must re-check the private approval record and withdrawal
inbox after that timestamp and immediately before committing. A revocation received before commit
cancels the quote. Do not seek approval for a quote whose eligible timestamp would fall after the
fixed withdrawal deadline. Once committed, a withdrawal received by the deadline removes the quote
from the next revision, but cannot remove it from earlier public git history.

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
> notes stay in a private folder that only the study team can open. They are deleted 30 days after
> the report is signed off, or at the applicable fixed deadline we give you, whichever comes first. A
> small private index is kept until that fixed deadline so your request code can still locate your
> contribution. Your individual probe answers and any participant-level paraphrase stay private.
> The public report contains aggregate coverage and de-identified aggregate paraphrase, not your
> screener, recruitment row or individual answers. We quote you only after showing you the exact text
> and waiting seven full days after your approval. You can revoke it with your code before it is
> committed. Earlier public git versions cannot be erased after a quote is committed.
>
> If you use assistive technology or particular display settings, you can tell us what you use so
> we can understand what we see, but you do not have to, and we will not ask why.
>
> You can skip any task or question, take a break, or stop at any time. If no study task begins, your
> withdrawal deadline is [exact no-show UTC deadline], calculated from the scheduled start we
> recorded before asking for consent. Your contact route and code verifier stay available through
> that exact time. If the session begins, we will record its start and give you the later exact
> participant deadline before collecting the first task response. Until the applicable deadline,
> you can send your request code to [private study contact] and ask us to withdraw you. We stop
> accepting new requests at that time, but finish an on-time request within seven days: we delete
> your private data, remove any linked material from the current report, recalculate
> the aggregates, and reassess decisions that used it. After that deadline the identity link is
> deleted, so we cannot locate or promise to remove one person's aggregate contribution. A revised
> report also cannot erase an earlier public git version.
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

1. By the applicable fixed UTC deadline, the participant sends the code to the private study contact. The
   recipient logs request time and acknowledges it without copying the code into public systems. An
   on-time request seals the minimum locator/evidence references needed to finish the case; that
   private case record expires no later than seven days after the deadline.
2. The study team verifies the salted code, freezes synthesis from that participant, and within 7
   days deletes recordings, notes, screener, consent/approval records, ID/contact/request-code entry and
   participant-to-evidence index entry. Two roles record completion in the non-identifying deletion
   ledger.
3. Using the evidence index before deleting it, remove the person's quotes and linked synthesis from
   the current report, recalculate every aggregate, and flag each affected decision for review. A
   committed quote may remain in old git history; the participant was told this before approval.
4. The person no longer counts. If a journey, findability, coverage or variant minimum falls below
   the gate, #265 reopens or remains open and a replacement uses the vacated assignment slot.
5. A request after the fixed deadline receives an explanation that the identity link and source
   material were deleted on schedule and participant-specific removal can no longer be executed.

For a person who was not recruited, the same private contact accepts a request-code query only until
the recorded screening deletion deadline. At that deadline the study deletes their screener answers,
contact, roster entry and request-code verifier together, records only a non-identifying deletion
receipt, and cannot perform a later candidate-specific lookup.

## No-session lifecycle

A candidate who declines, is found ineligible, is rejected, or never consents is non-recruited and
uses the screening deletion deadline. A person who consented but cancels or does not attend before a
study task begins is an **enrolled no-show**. Their fixed withdrawal deadline is the scheduled-session
start + 7 × 24 hours that was calculated and disclosed before consent. Retain their contact route and
salted request-code verifier through that exact time even when the screening deadline is earlier,
then delete the screener, contact, roster lifecycle fields, consent record and verifier together. No
participant-to-evidence index exists because no session evidence was collected. A replacement uses
the vacated private assignment slot only after this deletion state is recorded.

## Before findings are committed

The synthesiser checks every change to [validation-report.md](validation-report.md) for:

- no names, tags, contact details, paths, share links or credentials;
- no screener answers, IDs, per-person recruitment, eligibility, assignment or scheduling rows;
- no individual probe answers or participant-level paraphrases; public paraphrase is de-identified
  and aggregate across responses;
- quotes only after the exact-text approval, seven-day embargo and final revocation check;
- no detail that identifies a person in a small internal group, for example "the only person on
  the team who uses a screen reader" next to their play schedule. If a finding cannot be written
  without identifying someone, it is summarised at a level that does not.
