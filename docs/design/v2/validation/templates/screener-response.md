# Screener response

> Blank template. Fill a copy in the private study folder only. Give the participant the screener
> notice, private study contact and participant-held request code in
> [../participant-screening.md](../participant-screening.md) before the first question. Store only a
> salted verifier, never the raw request code. Never commit a filled copy, its filename or a
> per-person summary to public git.

| Field | Answer |
| --- | --- |
| Pseudonymous ID | |
| Date screened | |
| Private study contact and request code delivered | Yes / No |
| Salted request-code verifier reference | |
| Fixed screening deletion deadline (UTC; screening timestamp + 30 × 24 h) | |
| Q1 Time playing EFT | |
| Q2 Wipes with real progression | |
| Q3 Current play frequency | |
| Q4 Thinks about in raid | |
| Q5 Solo or squad | |
| Q6 Second screen, tablet or phone, and for what | |
| Q7 Companion tools used now | |
| Q8 Seen v2 concepts or helped plan | |
| Q9 Assistive technology or display settings (optional, participant's words) | |
| Q10 Devices available | |
| Q11 Recording preference | |
| Q12 Availability | |

## Scoring

| Criterion | Yes / No | Basis |
| --- | --- | --- |
| Counted (not pilot, author, moderator, note-taker or decision owner) | | |
| Expert | | |
| Regular or returning | | |
| Accessibility-relevant | | |
| Screen-reader user | | |
| Squad player | | |
| Second-screen user | | |
| Has not seen v2 concepts | | |
| Familiarity flag (helped plan) | | |

## If not recruited

At the fixed screening deletion deadline, delete this screener and the linked private contact,
recruitment-roster entry and salted request-code verifier together. Record only a non-identifying
deletion receipt (deadline, actual deletion UTC, count of records, verifier roles); do not retain a
participant ID, contact, code or filename in the receipt.

## Enrolled no-show or pre-session cancellation

If consent was recorded but no session begins, delete the contact, roster, consent record and
request-code verifier at the earlier of this screening deadline or scheduled-session start + 7 × 24
hours. Record only the lifecycle state and non-identifying deletion receipt; there is no
participant-to-evidence index to keep.
