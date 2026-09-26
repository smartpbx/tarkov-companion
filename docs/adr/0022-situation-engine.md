# ADR 0022: The situation engine

## Status

Accepted (#712, V3.0 package 0-2). Describes what exists on 2026-09-26.

## Context

V3 wants the app to know what the player is doing (queue, loading, raid, post-raid, a screen
between raids) and to switch its own surfaces with a one-line "because". V2 answered each of those
questions separately in each view model: the raid clock three times, squad state from coordinates,
"in raid" from whichever snapshot a page happened to read. Every V3 surface (the Now panel, sound,
the tablet, toasts) needs the same answer, with the same reason, at the same moment.

## Decision

1. **One situation, one service.** `SituationService` (Application) folds existing inputs into one
   immutable `Situation` (Core, `Domain/Situations`). Surfaces read it and its change stream; they
   do not ask the engines underneath.
2. **Inputs are the ones that already exist.** The runtime snapshot (raid state from
   `RaidStateService`, the log's party, the relay squad from `GroupSessionService`, the profile), the
   newest `ScanOutcome`, the objective route the Raid map opened, and a reported outcome. The raid
   rules stay in `RaidStateService`; the fold does not re-derive raid starts, ends, maps or sides.
3. **Four loading markers are read, nothing else new.** `Matching with group id:`, `MatchingCompleted:`,
   `LocationLoaded:` and `GameStarted:` (`RaidPhaseMarkerParser`) split matching from loading, and
   loading from the raid: the raid state enters "in raid" on `profileStatus Busy`, about a minute
   before `GameStarted`. They reach the service through `IEftLogObserver`, like the other log facts.
4. **Every fact has confidence, source, time and "because".** `SituationFact<T>` carries all four
   and `IsInferred`. A count (the raid clock from a start and a length) says `Count`/`Counted` and
   is never shown as the game's number; only a read extract screen is `Observed`.
5. **Dead and Extracted come only from a reported outcome.** The logs never say how a raid ended
   (`EFT_LOG_FACTS.md`), so the log alone reaches `PostRaid`. The one-tap question (T4) and a
   photographed summary screen report through `ReportOutcome`.
6. **Arrival order, not stamps.** "Did this marker come after the raid ended" is answered by an
   arrival counter, because the PC clock can step four hours mid-session (#891).
   `SituationService.RebaseClock` moves held times with the raid state's own rebase.
7. **Version means change.** `Version` rises by one when anything in the situation changes and
   never otherwise. Time left is stored as an anchor (`RemainingAt(now)`), so the clock ticks
   without new versions. A 5 s refresh lets time alone move the phase (post-raid falls back to the
   menu after 10 minutes, a between-raid screen after 3, an abandoned queue after 15).
8. **Transitions are logged with their evidence**: the service log and a 64-entry transition list,
   shown in Setup › Updates & Diagnostics in developer mode.
9. **Fixture timelines are the test style.** `tests/.../Situations/SituationTimeline.cs` feeds
   synthetic log lines, screenshot names, scans, relay states and clock steps through the app's own
   parsers and raid state and asserts the situation after each step.

## Not yet

- Stash and trader screens: `ScanContext` has no such kinds yet (T2). Flea, TASKS and an item are
  the between-raid screens today.
- Near-tie abstention ("Looks like you are looting, open?") and manual picks holding until the
  next raid boundary belong to the surface switcher built on this (0-4 onwards).
- `Because` strings are English; the Now panel localises from the fact kinds when it renders them.

## Boundaries

Inputs are the game's log files and screenshots on disk, and what squadmates' own companions share.
Nothing here reads game memory or traffic, sends input, or describes anybody who does not run the
companion.
