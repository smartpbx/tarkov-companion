# What Escape from Tarkov's logs actually contain

Established on 2026-09-11 against a live installation: 33 log folders, 254 raid record *lines*,
game build 1.1.5.0.47242. Everything here was measured, not assumed. Several entries record
an *absence*, which is as useful as a presence and stops the same ground being re-covered.

**Re-measured on 2026-09-26 against build 1.1.5.1.47510 (#403).** Six sessions played
2026-09-22..25, 80 MB, 64 files. The first section below has the counts; claims further down
that the re-measure overturned are marked **Stale (1.1.5.1)** where they stand.

Nothing here was taken from another tool's source. A third-party tool's published feature
list was used to decide what to look for; every claim below was then confirmed or refuted
against real logs.

## Re-measure on 1.1.5.1.47510 (2026-09-26, #403)

**The sample.** Six game launches, 2026-09-22..25, all PvP seasonal: every session wrote
`Session mode: PvpSeason` once, and 6,056 of the 6,151 backend requests went to the
`gw-pvp-season` host (89 to the lobby, 6 to `gw-pvp`). 53 online raids (distinct `shortId`, each with a `userConfirmed` and a `userMatchOver`) plus
two offline transit raids with no id (#892), so 55 scene presets. By profile: 34 PMC raids, 19
scav (all on Lighthouse). Ends: `Free` 44, `Transfer` 9, every Transfer on the scav profile. Maps
on the notifications: `Lighthouse` 19, `TarkovStreets` 12, `Shoreline` 7, `laboratory` 6,
`RezervBase` 5, `bigmap` 2, `factory4_day` 2. 103 flea sales, 9 quests started, 7 story quests
and 3 repeatables handed in, none failed.

**Not in the sample**, so still unmeasured on this build: a PvE session, a known death, a known
run-through, a finished craft, an insurance return (message type 8 never appeared). The outcome
findings below therefore rest on absence across 53 raids of unknown outcome, not on a death
that was looked for and not found.

Counts come from `tools/LogFactsAudit` (raids, sales, quests, load times) and from line-kind
counts taken by hand with every id, name and address masked. Nothing from these files is in the
repository; the format pack in `tests/TarkovCompanion.UnitTests/FormatGuards/Packs/1.1.5.1.47510`
holds synthetic lines in each shape.

| File | Lines | With a header | Opened by the companion | What in it is about the raid |
| --- | --- | --- | --- | --- |
| `application` | 7,234 | 6,436 | in full | map (scene preset, `profileStatus`), matching stages, eight load-stage timings, transit, profile selection, session mode, movement corrections |
| `backend` | 13,574 | 13,462 | in full | every notification (793), HTTP request paths (6,151) |
| `output` | 898,517 | 113,832 | in full (last 16 MB on replay) | a mirror of `application`, `backend`, `push-notifications` and `network-messages`, plus Unity's own: in-game clock, post-raid timing block |
| `push-notifications` | 166,072 | 1,603 | quest/flea lines only | the same notifications, payloads as indented JSON |
| `errors` | 120,034 | 21,538 | no | stack traces |
| `network-connection` | 583 | 583 | no | one connect/disconnect per online raid, with round trip and loss |
| `network-messages` | 1,576 | 1,576 | no | one counters line about every 30 s in a raid |
| `spatial-audio`, `files-checker` | 251, 126 | all | no | none |
| `inventory`, `player` | 43, 5 | 11, 5 | no | rejected inventory operations; names bots and players |
| `aiData`, `aiErrors`, `maperrors` | 143, 143, 34 | 53, 53, 34 | no | bot and map warnings, one session each |

Fourteen prefixes, not eleven: `aiData`, `aiErrors`, `maperrors`, `network-messages` and
`player` were each in one to six sessions; `objectPool` was in none.

**Notifications in `backend`** (distinct event ids; `output` carries the same 793 again):
`groupMatchRaidReady` 187, `groupMatchRaidNotReady` 161, `new_message` 126, `RagfairOfferSold`
103, `userConfirmed` 53, `userMatchOver` 53, `groupMatchStartGame` 50, `userMatchCreated` 34,
`RagfairNewRating` 12, `groupMatchInviteAccept` 7, `groupMatchUserLeave` 3 (5 lines),
`groupMatchWasRemoved` 1, `groupMatchInviteDecline` 1. `new_message` by message type: 4 (a
system message; 103 of them flea payments) 104, 12 (hand-in) 10, 10 (started) 9, 13 (a message with items, meaning unknown) 3.

**What the re-measure changed:**

- **Flea revenue is in the logs.** Each `RagfairOfferSold` is followed within a second by a
  `new_message` of type 4 whose `systemData` names the sold item and count and whose `items`
  hold one rouble stack with a `StackObjectsCount`: 103 sales, 103 payments, every one matched
  on time and count. The `systemData` also carries the buyer's nickname, a player never met, and
  must never be read. Not parsed yet.
- **One offer can sell several times.** The 103 sales named 72 offers; 18 offers sold in two to
  six parts, about a second apart, each part with its own event id and its own payment.
  `FleaSaleStateService` kept one row per offer and so dropped 31 of 103 sales; it now keys on
  the event id, and only the first copy of a sale reaches the raid record.
- **Group readiness is half read.** All 161 `groupMatchRaidNotReady` carry only an account id,
  and all 7 `groupMatchInviteAccept` carry the member at the top level; `GroupNotificationParser`
  looks for `extendedProfile` in both and reads nothing. A member who un-readies stays ready.
  (Fixed: both shapes are read; a not-ready line keys to the member's earlier ready line by `aid`.)
- **`push-notifications` gives the chat-only reader nothing.** Its payloads are indented JSON on
  the lines after `Got notification | …`, so no single line holds both the marker and the JSON:
  0 quest events and 0 sales from all six files. `backend` has them all, so nothing is lost.
- **`Status: Free` is never written.** All 53 `profileStatus` lines read `Status: Busy`, `RaidMode:
  Online`, `GameMode: deathmatch`. The end of a raid comes only from `userMatchOver`, the profile
  reload after an offline raid, and (unread) the `network-connection` disconnect, which fell
  within a second of `userMatchOver` (median 0.6 s over 52 pairs).
- **The player's own position appears in `application`**, 104 times: `Reason:PacketsQueue|Speed|
  Stuck|Lift, Position:(x, y, z), SpeedLimit:…, CurrentState:…`, written when the game corrects
  the player's movement. Sporadic (4 to 37 per session) and not read.
- **"DeathScreen" is not a death.** `output` writes a `[DevLog] === MENU LOAD PROFILE ===` block
  after every raid (52 opened with `PostRaid_Start`), and its `DeathScreen_Shown` milestone is the
  result screen, shown after every raid end. Outcome stays absent.
- **The in-game clock is written.** `RealDateTime:… GameDateTime:… factor:7`, 127 lines in
  `output`, about two per raid. Not read.
- **Map tokens:** `laboratory` now observed, on `profileStatus` (6) and as a scene preset (8).
- `ExitStatus`: 64 lines, all stack frames (54 with `TimeSpan`, 10 other frames). `SavageLockTime`:
  769 lines, none beside the player's own `profileid`. Queue time: 52 raids, 3.8 / 19.7 / 87.2 s
  (min / mean / max).

### Follow-up parser opportunities

Measured above. The first four rows are now read (#403 follow-up): the session mode moves the active
profile (`SessionModeParser`, `ProfileModeFollower`), the not-ready and invite-accepted shapes reach the
party (`GroupNotificationParser`), and the queue steps and spawn lines are situation stages
(`RaidPhaseMarkerParser`: `MatchingStep`, `Spawning`, `Spawned`; `userMatchCreated` and the other load
timings are still unread).

| Fact | Line kind | Frequency | Value for the V3 situation engine |
| --- | --- | --- | --- |
| Mode and season | `application` `Session mode: PvpSeason`; backend host `gw-pvp-season` | 1 per launch | High: picks the permanent or seasonal profile before any raid (#712 T1) |
| Squadmate un-readied / joined | `groupMatchRaidNotReady` (`aid` only); `groupMatchInviteAccept` (member at top level) | 161; 7 | High: the SQUAD block and the Matching phase show stale readiness today |
| Matching stages | `TRACE-NetworkGameMatching G`/`H`/`I`; `userMatchCreated` | 1 each per online raid; 34 of 53 raids | Medium: splits "matching" into finer stages |
| Load stages | `GamePrepared` … `PlayerSpawnEvent` … `GameSpawned` | 1 each per raid | Medium: "spawning" about 20 s before `GameStarted` |
| Flea revenue | `new_message` type 4, rouble stack in `items` | 1 per sale | Medium: roubles per sale on Debrief and in the flea-sold toast |
| Raid end, second source | `network-connection` `Disconnect`, `Statistics (rtt, lose)` | 1 per online raid | Medium: an end when `backend` misses one; per-raid connection quality |
| In-game time | `output` `GameDateTime:` | about 2 per raid | Medium: day/night for the NOW block without a screenshot |
| Own position | `application` `Reason:…, Position:(x, y, z)` | 4 to 37 per session | Low: sporadic; own position only |
| Post-raid screen timing | `output` `[DevLog] === MENU LOAD PROFILE ===` block | 1 per raid end | Low: written once back in the menu, so it marks the end of post-raid, not its start |
| Flea rating | `RagfairNewRating` | 12 | Low |

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

One folder per game launch. The newest is resolved from the folder's own name, which carries the
launch time, not from last-write time: file times drift when the clock steps, the name cannot.

Eleven file prefixes exist (**stale (1.1.5.1)**: fourteen, see the re-measure above). In a typical folder: `output`, `backend`, `application`,
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

## Which raid a line belongs to: `shortId`

Measured on 2026-09-20 over one player's day: 13 game launches (13 log folders), 9 `userConfirmed`,
8 `userMatchOver`. Deduplicated on `eventId`, as above.

- `userConfirmed`, `userMatchOver` and the `profileStatus` line all carry the game's own short id
  for the raid (`"shortId":"CX0FLS"` in the notification JSON, `shortId: CX0FLS` on the
  `profileStatus` line). **Every confirmation paired with an end of the same id, except one**:
  `CX0FLS`, a Streets raid whose game process died about 110 seconds in. No `userMatchOver` for
  it exists anywhere. Exit statuses of the eight ends: `Free` x7 and `Transfer` x1. The transfer
  was a scav raid on Lighthouse, and it did end the raid.
- **A raid outlives the log folder it started in.** Three of the nine raids were reconnected
  into after the game or the machine died, and their `userMatchOver` was written one, one and
  three launches after their `userConfirmed`, under the same id. A reconnect folder holds
  `LocationLoaded`, `profileStatus` and `GameStarted` and no `userConfirmed` at all. So a newer
  log folder does **not** by itself mean the raid in the older one is over; a different
  `shortId` does. The id on a reconnect's `profileStatus` line was redacted in the sample this
  was measured from, so "the reconnect line repeats the id" is inferred from the end pairing up,
  not read directly.
- **There is no shutdown marker.** All 13 folders simply stop, the clean exits included. A log
  that stops says nothing about whether the game crashed, and nothing may be decided from it.

What may be trusted about a raid nobody reported over: its id has a start and no end; another
id began after it; the game is not running; it began longer ago than any raid lasts. The
companion decides on exactly those (`RaidIdentity`, `RaidReplayDecision`, #568) and records such
a raid with the outcome "Unknown (not reported)", ended at the last moment it showed activity.

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
sold and how many is recoverable. No price or currency field is present in this notification.
**Stale (1.1.5.1):** revenue is recoverable, from the type-4 `new_message` that follows each sale
(see the re-measure above).
`FleaSaleParser` reads these, deduplicated on the notification's event id (on `offerId` until the
1.1.5.1 re-measure found one offer selling in parts); they are shown for the running session
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

What `Transfer` means in the game is only partly known. It appears on a bit over half of scav
runs and on no PMC run at all. At least some are transits to another map (below); whether all
are is not established.

**`Transfer` ends the raid.** It was read here as transit to another map with the raid
continuing, and a Streets raid was then watched ending with it and nothing following for the
rest of the session. Roughly one raid end in four carries it.

**Some `Transfer` ends are transits, and the raid after one can carry no short id (#892).** On 2026-09-23 a
scav Lighthouse raid ended `Transfer` and six minutes later the player was in The Lab. That raid,
and a second one straight after it, wrote no `userConfirmed`, no `userMatchOver` and no
`profileStatus` line. All each of them left in `application` was, in order:

```
MatchingCompleted:0 real:0 diff:0
scene preset path:maps/laboratory_preset.bundle rcid:laboratory.ScenesPreset.asset
LocationLoaded:10.97 real:18.09 diff:7.12
[Transit] Flag:None, RaidId:<id>, Count:0, Locations:laboratory ->
GameStarted:31.37(0) real:53.4(0) diff:22.03
```

and the end of the first was only `PrepareSelectedProfileLocally` / `CompleteSelectedProfile`, the
profile reload the game does on coming back to the menu. These were the only two `[Transit] Flag`
lines in six sessions (09-22 to 09-25); an online raid writes `[Transit] `<id>` Count:0` instead,
with no location. So the map comes from the scene preset, which is written before every raid
(55 of them in those sessions, tokens `city`, `customs`, `factory_day`, `laboratory`,
`lighthouse`, `rezerv_base`, `shoreline`), and a profile reload ends a raid that has no id.

**The stamp is the PC's clock, and it can step mid-session.** In two of those six sessions
every file stepped back about four hours at the same moment (Windows resynchronising a clock
that had shown UTC as local time). A file's own order stays right across the step; sorting
lines by stamp does not, so the startup replay keeps each file in written order and uses stamps
only to interleave files between steps.

## One claim tested and refuted

A third-party tool states that the logs are not written while a raid is in progress. That is
false for this installation. Measured across one 42-minute Shoreline raid: `output` wrote 768
lines and was never silent for more than a minute, `backend` 213 lines, `network-messages` 60.

`application` is the file that goes quiet in-raid, at 30 lines across those 42 minutes. So
watch `application` for the map and lifecycle, and `output` for liveness.

Live in-raid state is therefore possible: still-in-raid, connection quality, elapsed time.
Outcome and quest progress are not, and no amount of parsing will change that.

## The screenshot filenames, which are a separate source

The logs never carry the player's position (**stale (1.1.5.1)**: `application` writes it on a
movement correction, 104 times in six sessions; see the re-measure). The screenshot filenames do, and only when the
player takes one. Measured against the same installation on 2026-09-11:

```
2026-09-11[19-16]_80.02, 1.39, -51.06_-0.00242, 0.84404, 0.00393, 0.53626_9.91 (0).png
2024-02-08[20-26]_-185.0, 5.0, -412.7_0.0, -0.8, 0.1, -0.6 (0).png
2024-02-08[22-19] (0).png
```

Three shapes, all real. Position, then a facing quaternion; current builds add a further float
after the quaternion that older files do not have; and a screenshot taken outside a raid has no
coordinates at all, which is ordinary rather than a parse failure.

**The time in the name is the real time; the PC's clock was the one that was wrong.** It once
looked hours away from when the file was written. On 2026-09-22..25 (#891) the name agreed with
the relay's time to the minute while Windows ran four hours fast (a dual-boot RTC). The name has
minutes only. So the name's time is used, moved onto the PC's clock by the offset the relay
measured, or by whole hours when the gap is within minutes of whole hours, which is a clock or
zone error and never a live shot's age. Getting this wrong is not cosmetic: every position arrived
looking hours old, faded on the owner's map and published to the squad as stale.

**The folder is not fixed either.** An install leaves more than one plausible screenshots
folder on disk and writes to one of them, so the one holding the newest image is the one to
watch. As with the logs, change notifications cannot be relied on; the folder is polled.

## Re-measuring this note

The older sections were measured against build 1.1.5.0.47242, the re-measure at the top against
1.1.5.1.47510. DB4Tarkov's LOGS tool (the reason
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

**Format guards (#712 0-3).** Every line and name shape the parsers depend on has a synthetic
fixture per build in `tests/TarkovCompanion.UnitTests/FormatGuards/Packs/<build>/pack.json`; a new
build that changes a shape gets its own folder. At run time `FormatHealthMonitor` counts recognised
against unrecognised shapes and Setup › Updates & Diagnostics says when one changed.

## Still unknown

Three map tokens were never observed because those maps were not played in the logged window:
`terminal`, and the `Sandbox` variants beyond `Sandbox` and `Sandbox_high`. (`laboratory` was
seen on 1.1.5.1.)
The pairing now comes from synced data, so this matters less than it did.

Every `profileStatus` line, on all 254 record lines, reads `GameMode: deathmatch`, including on
ordinary raids; so did all 53 on 1.1.5.1. Unexplained. Nothing branches on it.
