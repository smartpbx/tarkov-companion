# What Escape from Tarkov's logs actually contain

Established on 2026-09-11 against a live installation: 33 log folders, 254 raid records,
game build 1.1.5.0.47242. Everything here was measured, not assumed. Several entries record
an *absence*, which is as useful as a presence and stops the same ground being re-covered.

Nothing here was taken from another tool's source. A third-party tool's published feature
list was used to decide what to look for; every claim below was then confirmed or refuted
against real logs.

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

The companion reads `application`, `output` and `backend`. `backend` carries the
`userConfirmed` and `userMatchOver` notifications that give an exact raid start and end, and
the group notifications, so it is opened deliberately for those. **`push-notifications` is
never opened.** Nothing in the game's logs is ever transmitted, bundled or attached.

Reading `backend` means the group blobs are in reach, so the boundary in `docs/SAFETY.md`
governs what is taken from them: the player's own party and their own looted dogtags, never a
squadmate's dogtags, which describe players the squadmate killed and the player never met.

## What is present and usable

| Fact | Where | Notes |
| --- | --- | --- |
| Map for the current raid | `application` | On the `TRACE-NetworkGameCreate profileStatus` line |
| Raid lifecycle | `application` | `LocationLoaded`, then `profileStatus` with `Status: Busy`, then `GameStarted` |
| Queue time | `application` | `MatchingCompleted:18.36 real:25.02 diff:6.66` — use `real` |
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

**Quest progress.** No completion or status events exist. Every `conditionCounter` hit is a
stack frame, and the only literal quest strings are two UI settings echoes. Quest tracking
stays local-first and manual, as ADR 0004 already specifies.

**Flea sales.** Only HTTP request and response logging for `ragfair` endpoints, with timing.
No sale or offer outcome.

**Scav cooldown.** `SavageLockTime` appears only inside group-notification blobs describing
*other* players, so it is read as a squadmate's own fact and never as the player's.

**Player side, corrected.** An earlier reading here said side was not derivable, because the
only `Side` fields belong to squadmates. That was right about those fields and wrong as a
conclusion. The account runs raids under two profiles, and only the signed-in one gets a
profile-selection line, so a raid notification carrying any other profile was a scav run. The
logs contain no word for this: `scav`, `pmcSide` and `IsScav` appear nowhere as role markers.
The two profile ids were stable across 33 sessions and five game versions, so this is used,
and always presented as inferred.

## One claim tested and refuted

A third-party tool states that the logs are not written while a raid is in progress. That is
false for this installation. Measured across one 42-minute Shoreline raid: `output` wrote 768
lines and was never silent for more than a minute, `backend` 213 lines, `network-messages` 60.

`application` is the file that goes quiet in-raid, at 30 lines across those 42 minutes. So
watch `application` for the map and lifecycle, and `output` for liveness.

Live in-raid state is therefore possible: still-in-raid, connection quality, elapsed time.
Outcome and quest progress are not, and no amount of parsing will change that.

## Still unknown

Four map tokens were never observed because those maps were not played in the logged window:
`laboratory`, `terminal`, and the `Sandbox` variants beyond `Sandbox` and `Sandbox_high`.
The pairing now comes from synced data, so this matters less than it did.

Every `profileStatus` line, on all 254 records, reads `GameMode: deathmatch`, including on
ordinary raids. Unexplained. Nothing branches on it.
