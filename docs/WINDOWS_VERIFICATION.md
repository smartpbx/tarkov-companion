# Windows verification

How the packaged Windows build is proven to work.

This describes the gate, not one run of it. It used to narrate a particular run's numbers,
which went stale the moment the next one finished and invited people to trust figures from a
build that no longer existed. What a given run found is in that run's own artifacts, and the
last section says how to read them.

Clayton's workstation may not build or run this project, and the development container is
Linux, so a GitHub-hosted Windows runner is the only sanctioned place to launch the package.
`.github/workflows/windows-verify.yml` does that on every push to the working branch and on
demand.

Escape from Tarkov is not installed on the runner and is not required. Nothing in this
workflow reads game memory, sends input to another process, or inspects network traffic.

Verifying and publishing are separate jobs. `windows-verify` builds, tests, launches and
photographs, and hands the result to `publish` as an artifact; `publish` is the only job with a
write token and it does not run for a pull request. So a pull request gets the whole gauntlet
and has neither a step that could publish nor a token that could.

## What the workflow does

1. Checks the verification scripts parse, runs `scripts/verify-windows-verification-static.ps1`
   (policy text, the gallery's interface-fault classifier, and the evidence sanitizer against
   fixtures), and after restore runs it again with `-SqliteLibraryPath` to drive the smoke's
   SQLite reader against a fixture database through the restored win-x64 `e_sqlite3.dll`.
   Then publishes the self-contained win-x64 archive with `scripts/package-windows.sh`, the
   same script the release path uses.
2. Extracts the archive the way a user would, into a directory that has never held the
   application.
3. Runs `--self-test` on a machine with no application data.
4. Runs the headless demo raid replay.
5. Deletes `%LOCALAPPDATA%\TarkovCompanion` so the next step is a genuine first run.
6. Checks the extracted package's `BUILD_INFO.txt` against the run's version and GitHub SHA
   (the installed copy is checked in step 9), then runs `scripts/windows-launch-probe.ps1`: starts
   `TarkovCompanion.exe` with no arguments, waits for a real main window, watches it for thirty
   seconds, captures the cropped companion window, requests a window close, and requires the
   process to exit cleanly. Success is decided only after the required local-data and database
   observations are recorded.
7. Runs `scripts/windows-page-gallery.ps1`: one launch per destination and map view, each with
   `TARKOV_COMPANION_UI_WARNING_LOG` pointed at its own file (see the gate table below). It also
   photographs every address in `V2RouteRegistry.Default` for Variant A at 1920x1080 and at
   3840x1080, reaching each one through the persisted preview address the way a deep link does.
   Sized launches pass `--window-size`, which leaves placement with the harness instead of
   restoring or overwriting the player's remembered per-monitor window bounds.
   These run before step 9 seeds any data, so they are the first-run state. A width the runner's
   desktop cannot offer is recorded as skipped rather than photographed cropped.
8. Runs `--self-test` again. Because the local data was wiped in step 5, a warm report
   showing a populated catalog is evidence that the desktop launches since then produced it.
9. Seeds a data-preservation marker, runs the packed installer silently, requires the marker
   to survive and the installed `BUILD_INFO.txt` to match the run, and self-tests the installed
   application.
   Then it updates that installation for real (#599): the same binaries packed as the next
   version, a local feed in `TARKOV_UPDATE_FEED`, `--apply-update-and-exit` started standing in
   `current\` with a child left running, and `current\BUILD_INFO.txt` must name the next version
   with the application reopened from it.
10. Runs `scripts/windows-smoke.ps1`, the developer diagnostic surface, beside the simulator. It
   reads the demo database read-only through the package's own `e_sqlite3.dll`, because Windows
   PowerShell 5.1 cannot load the net10.0 `Microsoft.Data.Sqlite` assembly. It waits for a
   migrated schema and then for the raid this launch opened, since a scan is recorded only
   against an open raid; takes the `raid_events` scan-row baseline; and after every fixture scan
   waits for that scan's committed row before sending the next, because the application writes
   history behind a queue and answers the scan before the row lands. A SQLite failure other
   than an initializing database is raised at once rather than retried until a timeout.
11. Uploads two artifacts, each kept for seven days. `windows-verification` carries only an
   allowlisted sanitized summary and bounded sanitized failure excerpts. `v2-route-gallery`
   carries the Variant A route captures from step 7, so a UI change can be looked at without a
   Windows machine; the Events capture is excluded because that page prints the local
   configuration directory, which carries the runner account name. Raw startup logs, SQLite
   databases, runner usernames, absolute paths, and the V1 page and map-view captures are never
   artifact evidence.
12. Fails the job on any unverified step. Only a run that passed uploads the package and, outside
   a pull request, the release payload; `publish` then runs only for `main` or a `v*` tag, and
   updates the rolling `dev` pre-release or cuts the tagged release.

A Linux `checks` job builds the solution, runs the full test suite, and runs the safety and
secret audits; `windows-verify` waits for it.

## What the gate checks

| Check | What passing means |
| --- | --- |
| Launch | the main window appears within the deadline and keeps responding for a 30-second watch |
| Shutdown | exit code 0, inside the deadline, with no hung process left behind |
| First-run sync | every json.tarkov.dev endpoint records `current` with no error |
| Page gallery | each launch reaches a responsive window with visible variation |
| V2 routes | every Variant A address reaches its own page heading at both widths, and the controls a player presses have bounding rectangles inside the window |
| Dead area | on the routes that declare a bound, the flat colour running in from the right edge stays under it; every other route is measured and reported so the next bound comes from numbers |
| Interface faults | no launch's toolkit wrote a `[Binding]`, `[Property]`, `[Visual]`, `[Layout]` or `[Control]` line, or a could-not-find/resolve/convert line from any other toolkit area, to its warning log; null-source bindings count |
| Warning capture | each launch's warning log received the application's own startup line before capture, so "no faults" was said by a listener that was running |
| Developer smoke | every fixture scan completes from the demo fixture and commits exactly one `scan` row, observed through a read-only SQLite reader |

Deliberately no numbers here. This file used to list the row counts and timings of one
particular run — items 5,320, map_spawns 3,018, "about eight seconds" — which were true of
that build and of no other. `map_spawns` has since been dropped entirely, so the table was
describing a schema that no longer exists, which is worse than describing nothing.

The gallery does not prove semantic expected-page selection, accessibility readiness, or
map-tile/data readiness. That #279 acceptance criterion stays open; it depends on the
application readiness signal owned by #281, and this workflow does not duplicate that work.

What a given run found is in that run's sanitized summary and failure-excerpt artifact.

## What this does not prove

- Anything that requires Escape from Tarkov to be running. Window discovery, GDI capture of
  the game window, screenshot and log watching, and OCR accuracy against real game visuals
  are all still unverified. `docs/LIVE_EFT_VALIDATION.md` remains the checklist for those.
- Behaviour at DPI scales other than the runner's, and on multiple monitors.
- Simulator file ingestion by the packaged app. The smoke drives demo fixture scans through
  the authenticated diagnostic channel while the simulator writes its files; nothing yet
  connects those files to the packaged app's watchers, so #279 stays open on that seam.
- That the application swallows no exceptions. The gallery gates the toolkit's warnings; the
  application's own `[Warning]` and `[Error]` log lines are not gated.
- Semantic page, accessibility, or map tile/data readiness (above).
- Nothing about a scan hotkey. There is no longer one to prove: `RegisterHotKey` appears
  nowhere in the source, and a scan is driven by the player taking a screenshot with the
  game's own key. `AGENTS.md` rule 8 forbids reintroducing one over a borderless game.

## Reading the evidence

Download the `windows-verification` artifact from the run. It contains
`windows-verification-summary.json` and, when something fails,
`windows-verification-failures.txt`; both are sanitized, every excerpt line is bounded, and
retention is seven days. The summary carries the launch timings (a main-window handle, not
semantic interactivity), per-launch gallery visual variation, warning-capture and interface-fault
counts, and the smoke's assertion counts, SQLite engine version and scan-row counts. The job
summary on the run page is written from that file and says nothing it does not contain. The raw
images, warning logs, startup log, and database stay on the ephemeral runner and are not
published.
