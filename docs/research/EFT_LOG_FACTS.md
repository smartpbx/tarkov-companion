# What Escape from Tarkov's logs actually contain

Established on 2026-09-11 against a live installation: 33 log folders, 254 raid record *lines*,
game build 1.1.5.0.47242. Everything here was measured, not assumed. Several entries record
an *absence*, which is as useful as a presence and stops the same ground being re-covered.

Nothing here was taken from another tool's source. A third-party tool's published feature
list was used to decide what to look for; every claim below was then confirmed or refuted
against real logs.

## Count unique events, not matching lines

Every notification is written **twice**, once into `output_000.log` and once into
`backend_000.log`, carrying the same `eventId` both times. A count of matching lines is
therefore double the number of things that happened, and two conclusions in this note were
originally stated at twice their true size before anyone noticed.

Deduplicate on `eventId` before counting. The real figure for the window described above is
**66 raids**, not the 254 raid record lines the header quotes; relative shapes such as the
per-map distribution survive the correction, absolute counts do not.

This matters to the reader as well as to the code. The companion watches both files and so
sees each notification twice; that is harmless because every consumer is idempotent, keyed on
the offer, the member or the raid rather than on arrival, but anything new that counts
arrivals has to account for it.

## Where the logs are

Inside the game's own install directory, not LocalLow and not Documents:

```
C:\Battlestate Games\Escape from Tarkov\Logs\log_<stamp>_<version>\<stamp>_<version> <prefix>_000.log
```

One folder per game launch. Resolve the newest by last-write time when watching starts, and
re-resolve when a new folder appears.

Eleven file prefixes exist. In a typical folder: `output`, `backend`, `application`,
`errors`, `network-messages`, `push-notifications`, `network-connection`, `spatial-audio`,
`files-checker`, plus `inventory`, `player` and `objectPool` in some sessions. Sizes vary
enormously between sessions: one `output_000.log` was 2.4 MB and another 15.6 MB.

## Privacy: what is in these files, and which are opened

`backend_000.log` and `push-notifications_000.log` carry large JSON blobs of real personal
data, for the player **and for anyone they grouped with**: nicknames, numeric account and
profile ids, full inventories, health state, and looted dogtags naming a killer and a victim.

The companion reads `application`, `output` and `backend` in full. `backend` carries the
`userConfirmed` and `userMatchOver` notifications that give an exact raid start and end, and
the group notifications, so it is opened deliberately for those. Nothing in the game's logs is
ever transmitted, bundled or attached.

**`push-notifications` and `notifications` are also now read narrowly** (package 47), having
previously not been opened at all. This is belt-and-braces rather than the fix: the quest events
are in `backend` and `output` as well, under a different name (see below). A line from one of those files is looked at only if it
already contains `ChatMessageReceived` or `RagfairOfferSold`, and is then offered to
`QuestNotificationParser` and `FleaSaleParser` and to nothing else. `GroupNotificationParser`
— the parser whose payloads are the group blobs described above — never sees a line from
them, and neither does the raid parser. `EftLogFiles` is the one place that decides this, and
the Setup self-test reports on exactly the files it names, so the report cannot describe a
file set the watcher does not use.

Why: on 1.1.5.1.47510 a session in which quests were demonstrably handed in produced 703
lines of `backend`, two recognised raids and **zero** `ChatMessageReceived`. The notifications
were there all along under the name `new_message`; see "Quest progress" below.

Reading `backend` means the group blobs are in reach, so the boundary in `docs/SAFETY.md`
governs what is taken from them: the player's own party and their own looted dogtags, never a
squadmate's dogtags, which describe players the squadmate killed and the player never met.

## What is present and usable

| Fact | Where | Notes |
| --- | --- | --- |
| Map for the current raid | `application` | On the `TRACE-NetworkGameCreate profileStatus` line |
| Raid lifecycle | `application` | `LocationLoaded`, then `profileStatus` with `Status: Busy`, then `GameStarted` |
| Queue time | `application` | `MatchingCompleted:18.36 real:25.02 diff:6.66` — use `real`. Read by `LoadTimeParser` and shown per raid on Debrief (package 26); it arrives before the raid it timed has an id, so the coordinator holds it until that raid starts. |
| Still in a raid | `output` | HTTP keepalive about every 60s, network stats about every 30s |
| Active profile | `application` | `CompleteSelectedProfile ProfileId:… AccountId:…` |
| Game version | everywhere | Pipe field 2, the folder name, the filename, and `Init: pstrGameVersion:` |

The map arrives on the `profileStatus` line roughly half a second after `LocationLoaded` and
about a minute before `GameStarted`, so it is the earliest reliable point to switch maps.

Map tokens are **not** consistently cased: `bigmap` and `factory4_day` are lowercase while
`TarkovStreets`, `Woods`, `Interchange`, `Shoreline`, `Sandbox`, `Sandbox_high`, `Lighthouse`
and `RezervBase` are capitalized. Comparison is case-insensitive, and the token-to-map pairing
is read from json.tarkov.dev's `nameId` rather than hand-maintained.

## What is absent, and must not be faked

**Raid outcome.** Survived, died, run-through: none of it is written. The only `ExitStatus`
occurrences are .NET stack frames for a method signature that also carries a `TimeSpan`, which
proves the client holds both and simply never records them. `Killed` appears only inside a
looted dogtag's JSON and describes some other player's death. Raid history therefore stores no
outcome, and that is correct rather than a gap. Duration is still derivable from timestamps.

**Quest progress is present, and this section was stale.** It previously said no completion or
status events exist, on the strength of a search for `conditionCounter` (every hit of which
really is a stack frame) and two literal quest strings that turned out to be UI settings
echoes. That search never looked at the notification the game actually sends: each quest
starting, failing or being handed in arrives as a websocket notification — the same kind the
flea sales below arrive as — with the message's `type` field at 10, 11 or 12 and a `templateId`
whose first word is the quest's own (24-character) id.

**The announcement has two spellings, and which one you see depends on the file.** This note
previously said the event "arrives as a `ChatMessageReceived` notification" and cited 380 such
lines across eight log folders. That measurement was taken against `push-notifications_000.log`
— the one file the companion deliberately does not open in full. Re-measured on
1.1.5.1.47510, session `log_2026.09.18_22-11-05`:

| File | Announces the quest event as | In this session |
| --- | --- | --- |
| `push-notifications_000.log` | `ChatMessageReceived` | present, not read in full (privacy) |
| `backend_000.log` | `new_message` | 14 lines; **zero** `ChatMessageReceived` |
| `output_000.log` | `new_message` | present |
| everything else | — | none |

The payload is identical either way, and unchanged on 1.1.5.1: a `new_message` envelope whose
`message` object carries `_id`, `type` and `templateId`. Verbatim, truncated:

```
2026-09-18 22:57:50.161|1.1.5.1.47510|Info|backend|WebSocketSharp - message received: NOTIFICATION 6aadc1ee83c0d7b85607ca2d new_message
[{"type":"new_message","eventId":"…","dialogId":"…","message":{"_id":"…","uid":"…","type":12,"dt":1789772270,"text":"quest started","templateId":"60896bca6ee58f38c417d4f2 successMessageText","items":{…
```

`QuestNotificationParser` accepted only `ChatMessageReceived`, so in the files it was actually
reading it rejected every quest event on its first substring scan. It now accepts either
spelling, with the shape checks behind it unchanged. **Key on `message.type`, never on `text`:**
that field reads `"quest started"` on the hand-ins too. In the raid above, `type` was 10 nine
times, 12 four times, 13 once, and 11 not at all. `QuestNotificationParser` reads them and `QuestLogProgressService` applies them to
recorded progress, deduplicated on the message's own `_id` so a redelivered notification is not
acted on twice and a quest never moves backwards out of Completed or Failed.

Reading the line was never the hard part. **The watcher starts every already-existing file at
its end**, so the tail only delivers what is appended after the first poll, and everything
written before that is recovered by a separate startup replay — which, until package 47, parsed
each replayed line for raid evidence and never handed it to the observer. A companion started
after the game (the ordinary case) therefore recognised the raid and dropped every quest in it.
The replay now feeds the same parsers the tail does. That is safe to repeat because each
observation is idempotent: a quest carries its own event id, a re-recorded state reports itself
unchanged, a sale is keyed by its offer id. It is bounded to the last 16 MB of each file. The objectives
inside a quest are not covered by this: only the quest's own state moves. Quest tracking is no
longer manual-only, though a JSON import and a TarkovTracker token remain as ways to seed
progress the game has not yet announced (ADR 0004).

**Flea sales are present, and an earlier entry here was wrong.** This note previously said
only `ragfair` HTTP traffic existed and no sale outcome. That came from searching for the
phrase "offer sold", which never appears; the notification type is one word. There are 52
`RagfairOfferSold` notifications, carrying `offerId`, `handbookId` and `count`. So which item
sold and how many is recoverable. No price or currency field is present, so revenue is not.
`FleaSaleParser` reads these, deduplicated on `offerId`; they are shown for the running session
on the V1 Flea page and, per raid, on Debrief (package 26).

**The player's own inventory.** Measured across 1390 notification lines in the six newest log
folders. 182 carry a top-level lowercase `profileid`, which is the marker that a notification
is the signed-in player's own; not one of those also carries an `Items` array or a `Dogtag`
object. Ten lines contain a `Dogtag`, and not one of those carries `profileid`. Every shape
that carries an inventory hangs it under `extendedProfile`, which is the squadmate envelope.

The split is structural rather than incidental, and it settles a feature: there is no kill
list to build. The only dogtags in these files are inside other people's bags, naming players
this player never met, which `docs/SAFETY.md` puts out of bounds. The reader that could have
parsed them has been removed rather than left to be pointed at the wrong inventory later.

A caution on method, because the first pass got this wrong: a case-insensitive search for
`profileid` also matches `ProfileId` and `KillerProfileId` inside dogtag objects, which makes
squadmate blobs look like they carry the player's own marker. The distinction is case
sensitive.

**Scav cooldown.** `SavageLockTime` appears only inside group-notification blobs describing
*other* players, so it is read as a squadmate's own fact and never as the player's.

**Player side, corrected.** An earlier reading here said side was not derivable, because the
only `Side` fields belong to squadmates. That was right about those fields and wrong as a
conclusion. The account runs raids under two profiles, and only the signed-in one gets a
profile-selection line, so a raid notification carrying any other profile was a scav run. The
logs contain no word for this: `scav`, `pmcSide` and `IsScav` appear nowhere as role markers.
The two profile ids were stable across 33 sessions and five game versions, so this is used,
and always presented as inferred.

**A second side signal, one-way.** The `status` on `userMatchOver` is either `Free` or
`Transfer`, and across 66 deduplicated raids they fall out like this:

| | PMC | scav |
| --- | --- | --- |
| `Free` | 39 | 11 |
| `Transfer` | 0 | 16 |

So `Transfer` proves a scav run, 16 times out of 16, and is the only thing in these files that
establishes side outright rather than by inference. The converse does not hold: 11 of the 27
scav raids ended `Free`, so `Free` proves nothing about either side. The companion uses the
implication in the one direction it holds, and reports a disagreement rather than resolving it
when the status says scav and the profile says PMC.

What `Transfer` means in the game is still unknown. It appears on a bit over half of scav runs
and on no PMC run at all, which is the shape of a particular kind of scav exit rather than of
scav runs in general. That is a guess and nothing depends on it.

**`Transfer` ends the raid.** It was read here as transit to another map with the raid
continuing, and a Streets raid was then watched ending with it and nothing following for the
rest of the session. Roughly one raid end in four carries it.

## One claim tested and refuted

A third-party tool states that the logs are not written while a raid is in progress. That is
false for this installation. Measured across one 42-minute Shoreline raid: `output` wrote 768
lines and was never silent for more than a minute, `backend` 213 lines, `network-messages` 60.

`application` is the file that goes quiet in-raid, at 30 lines across those 42 minutes. So
watch `application` for the map and lifecycle, and `output` for liveness.

Live in-raid state is therefore possible: still-in-raid, connection quality, elapsed time.
Outcome and quest progress are not, and no amount of parsing will change that.

## The screenshot filenames, which are a separate source

The logs never carry the player's position. The screenshot filenames do, and only when the
player takes one. Measured against the same installation on 2026-09-11:

```
2026-09-11[19-16]_80.02, 1.39, -51.06_-0.00242, 0.84404, 0.00393, 0.53626_9.91 (0).png
2024-02-08[20-26]_-185.0, 5.0, -412.7_0.0, -0.8, 0.1, -0.6 (0).png
2024-02-08[22-19] (0).png
```

Three shapes, all real. Position, then a facing quaternion; current builds add a further float
after the quaternion that older files do not have; and a screenshot taken outside a raid has no
coordinates at all, which is ordinary rather than a parse failure.

**The time in the name is not usable.** It ran hours away from when the file was written, and
the zone it is in was never established. Take the coordinates from the name, because only the
name has them, and take the time from the filesystem. Getting this wrong is not cosmetic: every
position arrived looking older than the raid already on screen and was discarded, so the
feature appeared dead while working correctly.

**The folder is not fixed either.** An install leaves more than one plausible screenshots
folder on disk and writes to one of them, so the one holding the newest image is the one to
watch. As with the logs, change notifications cannot be relied on; the folder is polled.

## Re-measuring this note

Everything above was measured against build 1.1.5.0.47242. DB4Tarkov's LOGS tool (the reason
these claims were revisited for issue #403) also targets 1.1.5.0, so the outcome and scav-cooldown
findings below do not need a newer build to be re-checked — only a fresh, larger sample.

`dotnet run --project tools/LogFactsAudit -- <folder>` reads a copy of the game's `Logs`
directory (or any folder holding `log_<stamp>_<version>` session folders) through the same
parsers the companion runs in production, and reports counts for each fact this note discusses:
raids started/ended, run-through endings, `ExitStatus` and `SavageLockTime` line counts (with
the same stack-frame and own-profile heuristics this note used by hand), quest events by state,
flea sales, and queue/load times. It reads local files only, never touches the game process, and
exits cleanly with no argument or a folder that does not exist — it is a developer aid, not part
of `scripts/build.sh` or `scripts/test.sh`, the same way `tools/V2RenderPreview` is not. Update
the counts and conclusions above from its output rather than from a fresh manual `grep`, so the
same double-counting and case-sensitivity mistakes this note already paid for cannot recur.

## Still unknown

Four map tokens were never observed because those maps were not played in the logged window:
`laboratory`, `terminal`, and the `Sandbox` variants beyond `Sandbox` and `Sandbox_high`.
The pairing now comes from synced data, so this matters less than it did.

Every `profileStatus` line, on all 254 record lines, reads `GameMode: deathmatch`, including on
ordinary raids. Unexplained. Nothing branches on it.
