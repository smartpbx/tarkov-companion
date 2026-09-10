# Windows verification

How the packaged Windows build is proven to work, and what the current run proved.

Clayton's workstation may not build or run this project, and the development container is
Linux, so a GitHub-hosted Windows runner is the only sanctioned place to launch the package.
`.github/workflows/windows-verify.yml` does that on every push to the working branch and on
demand.

Escape from Tarkov is not installed on the runner and is not required. Nothing in this
workflow reads game memory, sends input to another process, or inspects network traffic.

## What the workflow does

1. Publishes the self-contained win-x64 archive with `scripts/package-windows.sh`, the same
   script the release path uses.
2. Extracts the archive the way a user would, into a directory that has never held the
   application.
3. Runs `--self-test` on a machine with no application data.
4. Runs the headless demo raid replay.
5. Deletes `%LOCALAPPDATA%\TarkovCompanion` so the next step is a genuine first run.
6. Runs `scripts/windows-launch-probe.ps1`: starts `TarkovCompanion.exe` with no arguments,
   waits for a real main window, watches it for thirty seconds, screenshots the desktop,
   requests a window close, and requires the process to exit cleanly.
7. Runs `--self-test` again. Because the local data was wiped in step 5, a warm report
   showing a populated catalog is evidence that the desktop first run produced it.
8. Runs `scripts/windows-smoke.ps1`, the developer diagnostic surface, against the simulator.
9. Publishes every report, the screenshot, the startup log, and the resulting SQLite database
   alongside the package.

A parallel Linux job builds the solution, runs the full test suite, and runs the safety and
secret audits.

## Run 34535907475, commit 465fa62

Every step passed.

| Observation | Result |
| --- | --- |
| Main window appeared | 2.3 seconds after launch |
| Window title | Tarkov Companion |
| Stability | alive and responding for the full 30-second watch |
| Shutdown | exit code 0, well inside the deadline |
| Desktop | 1024x768, interactive session |

First-run data, from the database the run published:

| Table | Rows |
| --- | --- |
| items | 5,320 |
| item_sell_offers | 25,511 |
| quest_catalog_tasks | 515 |
| map_spawns | 3,018 |
| map_extracts | 152 |
| maps | 17 |
| hideout_stations | 26 |
| traders | 16 |

All seven json.tarkov.dev endpoints recorded `current` with no error, and the whole sync
finished in about eight seconds. The warm self-test reported 5,320 normalized items and a
recognition catalog of the same size. Tesseract initialized on the runner.

## What this does not prove

- Anything that requires Escape from Tarkov to be running. Window discovery, GDI capture of
  the game window, screenshot and log watching, and OCR accuracy against real game visuals
  are all still unverified. `docs/LIVE_EFT_VALIDATION.md` remains the checklist for those.
- Behaviour at DPI scales other than the runner's, and on multiple monitors.
- The global scan hotkey, which no code path registers yet.

## Reading the evidence

Download the `windows-verification` artifact from the run. `launch-probe.png` shows the
running application, `launch-probe.json` records the window and shutdown observations,
`self-test-warm.json` records what the first run produced, `startup.log` records the
application's own lifecycle entries, and `tarkov-companion.db` is the database the first run
built.
