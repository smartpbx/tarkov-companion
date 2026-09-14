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

## What the gate checks

| Check | What passing means |
| --- | --- |
| Launch | the main window appears within the deadline and keeps responding for a 30-second watch |
| Shutdown | exit code 0, inside the deadline, with no hung process left behind |
| First-run sync | every json.tarkov.dev endpoint records `current` with no error |
| Page gallery | every page is opened, photographed, and checked for having drawn anything |
| Binding warnings | a page that asks for a property it does not get fails the step |

Deliberately no numbers here. This file used to list the row counts and timings of one
particular run — items 5,320, map_spawns 3,018, "about eight seconds" — which were true of
that build and of no other. `map_spawns` has since been dropped entirely, so the table was
describing a schema that no longer exists, which is worse than describing nothing.

What a given run found is in that run's own artifacts.

## What this does not prove

- Anything that requires Escape from Tarkov to be running. Window discovery, GDI capture of
  the game window, screenshot and log watching, and OCR accuracy against real game visuals
  are all still unverified. `docs/LIVE_EFT_VALIDATION.md` remains the checklist for those.
- Behaviour at DPI scales other than the runner's, and on multiple monitors.
- Nothing about a scan hotkey. There is no longer one to prove: `RegisterHotKey` appears
  nowhere in the source, and a scan is driven by the player taking a screenshot with the
  game's own key. `AGENTS.md` rule 8 forbids reintroducing one over a borderless game.

## Reading the evidence

Download the `windows-verification` artifact from the run. `launch-probe.png` shows the
running application, `launch-probe.json` records the window and shutdown observations,
`self-test-warm.json` records what the first run produced, `startup.log` records the
application's own lifecycle entries, and `tarkov-companion.db` is the database the first run
built.
