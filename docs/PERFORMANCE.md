# Performance

The companion runs on a second 1920x1080 screen while somebody plays, with screenshots arriving, the
game's logs being read, the relay exchanging and the map redrawing. What matters is that it stays
smooth for a whole raid, so that is what is measured: not a benchmark of one function, but what the
running app does over forty minutes.

Two things measure it, and they answer different questions.

| | What it answers | Runs where |
| --- | --- | --- |
| `tools/RaidPerfHarness` | How long does it take, how busy is it, does anything grow? | On the dev box, by hand |
| `tests/TarkovCompanion.UnitTests/Performance` | Has something got worse than we last measured? | In CI, on every PR |

## The harness

Boots the real composition and the real V2 window in headless Avalonia with Skia drawing, then drives
the Raid workspace. It is not in `TarkovCompanion.sln`, like the render preview beside it, so it is
restored and built on its own.

```bash
export PATH=/root/.dotnet:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 AVALONIA_TELEMETRY_OPTOUT=1 \
  NUGET_PACKAGES=/tmp/tarkov-companion-nuget DOTNET_CLI_HOME=/tmp/tarkov-companion-dotnet-home
P=tools/RaidPerfHarness/TarkovCompanion.RaidPerfHarness.csproj
flock -o /tmp/tarkov-build-0.lock dotnet restore $P -p:EnableWindowsTargeting=true --disable-build-servers
flock -o /tmp/tarkov-build-0.lock dotnet build $P -c Release --no-restore --disable-build-servers -p:EnableWindowsTargeting=true
dotnet tools/RaidPerfHarness/bin/Release/net10.0/TarkovCompanion.RaidPerfHarness.dll \
  --seed-database <a synced tarkov-companion.db> --json out.json
```

Without a seeded database the demo fixture has no map overlays, so the plan is nearly empty and the
numbers mean little. See `render-preview-seed-database` in the notes for where a synced one lives.

| Scenario (`--scenarios`) | What it does |
| --- | --- |
| `cold` | Milliseconds from process start to: `main`, composition built, platform ready, window shown, first paint, data ready, usable. `--cold-repeat N` runs it in N fresh processes and reports the median and spread. |
| `map` | Opens the Raid workspace and times it to the first frame with the map in it, then switches to a second map. |
| `pan` | Drags and zooms the plan with real pointer events and draws a frame after each. Once quiet, and once with the raid's own events landing at ten times their rate. |
| `raid` | A raid, sampled once a raid-minute: CPU, managed heap after a full collection, allocation, scene rebuilds, and what each kind of event cost. `--raid-minutes 40 --group 1` or `--group 6`. |
| `bisect` | Idle allocation with and without frame capture, and on another page. |
| `plansearch` | Typing in the Plan workspace's quest search, with the filter set to All so the whole board is read: one keystroke (setter and settled), the whole query typed at speed, and how many row and group view models a keystroke kept. Needs no map, so it runs on its own in about a minute. `--plan-query "graphics card"`. |

`--accelerate N` delivers the raid's events N times as fast, which answers "what accumulates" in a
fraction of the time and does not answer "how busy", because the application's own one-second timers
do not speed up. `--alloc-types` samples which types allocate; `--scene-diff` names the fields that
differ between two builds of a scene.

### What the numbers are

CPU-rasterized frames in headless Avalonia, on a shared box: there is no GPU and no Windows
compositor, so an absolute frame time is not what a player sees. Counts do not depend on the renderer
and hold anywhere: scene rebuilds per event, view models recreated, bytes allocated, heap growth. Read
timings as a comparison between two builds run at the same time, and read the load average the report
prints beside them.

Events are published from a thread-pool thread, because that is where the application's own
publishers run. The composition's real group session is stopped first: with sharing off it republishes
"not sharing" every five seconds, which wipes the simulated squad off the plan.

## What it found

The first measurements, on Customs with a full layer set, were not what anybody expected.

- **One screenshot, group exchange or log line rebuilt the plan twelve to twenty-nine times.** The
  Raid cockpit rebuilt its whole scene for every publication of the runtime store and every map
  property change, none of them coalesced. A screenshot publishes the raid, the screenshot's name, the
  outbox, the supervisor and more, and changes the player marker, the trail and the group's marks in
  one turn. Now: one rebuild per dispatcher turn (`DeferredDispatch`), and none when neither the raid
  nor the group changed.
- **An idle raid rebuilt the plan about once a second.** `RuntimeStateStore` froze every slice again
  on every publication, boxing a fresh `ImmutableArray` of the trail each time, which defeated
  `MapViewModel.ShowPlayer`'s own "nothing changed" guard. Untouched slices now keep their instance,
  and the guard compares the trail's points.
- **The renderer's change detection never said "unchanged".** `MapSceneObject` holds its floor ids and
  points in arrays, a record compares arrays by reference, and every rebuild builds new ones, so a
  scene rebuilt from the same inputs always differed and every present recreated every marker, line
  and label. `HasSameDisplayAs` compares what is drawn and ignores when it was observed.
- **Tile artwork was re-resolved on every tick.** Its asset carried the time of the rebuild.
- **A squadmate's age, to the second, changed at every exchange.** Now in fifteen-second steps.

The trail was already capped at 240 points (`RaidStateService`), the group's per-member trail at ten
(`GroupSessionService`) and the recent screenshot names at three; the `raid` scenario reports the
managed heap after a full collection once a raid-minute and its slope, which is how growth is checked.

### Typing in the quest search (package 45)

Over a synced catalog — 503 quests, 1,758 objectives, 17 map groups, filter All — `plansearch` on the
same harness build, before and after:

| | Before | After |
| --- | --- | --- |
| One keystroke, in the setter (mean) | 12-16 ms | 0.005 ms |
| One keystroke, settled (mean / p50) | 29-51 ms / 30-43 ms | 16-17 ms / 8 ms |
| One keystroke, allocated | 1,592-1,868 KB | 411-413 KB |
| Typing "graphics card" (13 characters), blocked | 140-144 ms | 0.02 ms |
| Typing "graphics card", settled | 170-175 ms | 33.5 ms |
| Typing "graphics card", allocated | 18.3 MB | 0.26 MB |
| Filter ran on the keystroke's own stack | yes | no |
| Rows kept by a keystroke that changes nothing | 0 of 9 | 9 of 9 |

Three faults, all of them "the whole board, per character": the searchable text of every quest was
joined inside the filter; every group and row view model was constructed again, with its commands; and
the Home overview listened for any property at all on the workspace, so it re-projected every
objective on the board twice per keystroke. What is left in "settled" is the work a changed result set
really does ask for — new rows for what is now shown, and the item-name reads for the newly selected
map.

The filter is posted at `DispatcherPriority.Background` (`UiThreadPost.BehindInput`), not through
Avalonia's own context, which posts at `Default` — above `Input`. Deferring through that takes the
filter off the setter's stack but still runs it once per character, ahead of the next key, so the box
stays as slow to type in as it was. Nothing installs a synchronization context: `UiThreadPost` hands
`DeferredDispatch.Posting` a delegate, and `UiThreadPostTests` holds that decision, including that
making it leaves `SynchronizationContext.Current` alone.

## The budgets

`RaidPerformanceBudgetTests` fails if any of these regress. Byte budgets are about twice the measured
cost and do not move with the machine. Time budgets are about ten times, because a CI runner is slower
and noisier than the box they were measured on; they catch an order-of-magnitude regression, not a
small one.

| | Measured | Before the fixes | Budget |
| --- | --- | --- | --- |
| Publishing a slice the plan does not draw | 432 B | 3,816 B | 1,024 B |
| Presenting a rebuilt scene that did not change | 10,104 B, nothing recreated | 453,078 B, everything recreated | 20,000 B |
| Presenting a scene where one marker moved | 453,062 B | 453,080 B | 900,000 B |
| First present of a 250 + 120 + line scene | 1.25 MB, 3-6 ms | | 2.5 MB, 100 ms |
| Composition and first window view models | 460-560 ms | | 6,000 ms |

`PlanSearchBudgetTests` holds what one keystroke in the quest search costs, per quest on the board,
because the fault it exists for was reading every quest again for every character.

| | Measured | Before the fix | Budget |
| --- | --- | --- | --- |
| One keystroke, result set unchanged | 13,822 B — 26 B per quest | about 3.7 KB per quest | 64 B per quest |
| One keystroke, result set narrowed | 17,912 B — 34 B per quest | about 3.7 KB per quest | 64 B per quest |
| Joining every quest's searchable text | 415 KB, 0.5 ms, once per board read | once per keystroke | 1 MB, 50 ms |

The last two are the part of "cold start" and "first map draw" that a process with no rendering
platform can measure. The window, the artwork and the frame are in the harness's `cold` and `map`
scenarios.

A failing budget is a prompt to run the harness and find out why, not to raise the number.

## Known slow, not fixed here

- **A real change to the plan recreates all of it.** Moving one marker recreates every marker, line
  and label view model, and the item controls recreate their visuals: about 450 KB in a unit test and,
  on a full Customs plan in the harness, tens of MB and a few hundred milliseconds on the UI thread.
  It now happens once per real change (a screenshot, a teammate moving) instead of a dozen times per
  event, but it is the next thing to fix: keep the marker collections stable and add, remove and
  replace only what changed.
- **The V2 shell refreshes about ninety properties every second** and again for every store
  publication. Raising only the ones that changed measured about 40% less idle allocation and no
  difference per event, so it was left alone.
- **Cold start is dominated by data readiness**, not by the window. With a fully synced catalog (5,321
  items, 515 tasks, a 94 MB database) the window was shown at 1.7-2.3 s and painted at 1.9-2.6 s, but
  the data was ready at 8.7-11.1 s, on a box under load and headless. Nothing here has broken that
  seven-to-nine second gap down yet, and it is the first thing to look at for a faster start.

## The interface thread and SQLite (#453)

Microsoft.Data.Sqlite's `...Async` calls are synchronous, so a repository awaited from the dispatcher
ran on the dispatcher. `SqliteConnectionFactory.OpenAsync` now leaves any thread that is not a pool
thread, which moves the whole repository call with it; loads that look up one row per item run as a
whole through `OffInterfaceThread.Run`. Measure with the render tool's `--ui-stalls <ms>` (add
`--ui-stalls-before-show` to leave first layout out of it): startup's longest dispatcher turn went
from 2.3-3.1 s to 0.3-0.9 s over three runs each, headless, on a box at load 9.

## Gotchas

- A view that is not visible is nearly free. Bisecting by leaving out the property that shows the
  Raid workspace made every event look cheap because the map was hidden.
- `CaptureRenderedFrame` allocates an 8 MB bitmap natively per call. It does not show in managed
  allocation counters, and it is why the harness draws a frame only when the scene was presented.
- The first call of anything pays for the JIT. The budget tests warm up first; a cold number is
  the harness's job.
