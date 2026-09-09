# Agent K — simulator and behavioral Windows smoke remediation

Work from current `origin/main` only after runtime composition, recognition remediation, and tarkov.dev map work have been integrated. Read `AGENTS.md`, the autonomous handoff simulator/smoke sections, and the architecture/recognition review reports. Commit the result and report the hash plus verification evidence.

## Goal

Make the separate EFT simulator and Windows smoke harness prove the real companion pipeline rather than process liveness or echoed fixture fields.

## Deliverables

1. Render distinct, deterministic, non-game synthetic scenes for item inspect, ambiguous item, extract list with statuses, mixed container grid, flea rows, loading/raid state, inventory, and unknown context. Use realistic layout variation at the supported resolution/UI-scale matrix without copying EFT art.
2. Emit real PNG screenshots (not zero-byte markers) and log lines in the same proven/parser-supported grammar consumed by the companion. Clearly label synthetic evidence.
3. Route simulator captures through the production OCR/scan use case in demo/developer mode. Fixture OCR may be used only for explicitly post-OCR unit tests, never the behavioral smoke result.
4. Expand the authenticated diagnostic response to return structured recognition result, canonical item IDs, confidences, extract statuses, container ambiguity/partial value, flea rows, current map, last-known position, and provenance needed by the smoke harness.
5. Rewrite `scripts/windows-smoke.ps1` to begin with a fresh unpack of the built release zip, run `--self-test`, launch the simulator and companion in an interactive desktop, trigger real diagnostic scans, and assert the handoff §33.5 behaviors: resolved item, ambiguity policy, map selection, position/heading, extract set/status honesty, container total/flagged cells, flea parsing, offline cached relaunch, file watcher/hotkey/monitor/DPI/DPAPI checks, and safety flags.
6. The report JSON must identify each assertion and its evidence, distinguish simulated vs. native/real-EFT validation, and exit nonzero on any required failure. No hardcoded success booleans disconnected from observed behavior.
7. Add Linux-capable integration tests around the same fixture flow where native Windows APIs are abstracted, plus Windows-only smoke tests for actual native behavior.

## Constraints

- The simulator remains a separate process and is excluded from game detection/capture targeting.
- No game installation or live EFT process is required.
- No gameplay input automation; diagnostic IPC is app-owned and developer-token gated.
- No screen-coordinate clicking in the harness.
- Screenshots stay local and are deleted/retained only according to explicit test-output settings.

## Acceptance

- A packaged Windows run produces a green JSON report only after real behavioral assertions pass.
- Item/extract/container/flea results originate from rendered pixels through the production OCR provider.
- Map/position outcomes originate from the companion's real parsing/state services.
- Offline restart proves a new process reads persisted cache.
- Build and all tests pass with zero warnings.
