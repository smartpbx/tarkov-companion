# Tarkov Companion v1.0 release report

Status: In progress. This document must not be read as a completed release claim until every non-live-EFT gate below is verified.

## Build identity

- Version: `1.0.0`
- Source: `https://github.com/smartpbx/tarkov-companion` (private)
- Commit: populated at package time
- Package: `dist/TarkovCompanion-v1.0.0-win-x64.zip`
- SHA-256: populated at package time

## Environments

- Build host: Omarchy 4.0.0 / Arch Linux x86-64, .NET SDK 10.0.401, runtime 10.0.12.
- Windows validation: dockur Windows 11 VM; exact build/hardware data populated by the smoke report.
- Real game: not installed in the VM; direct EFT validation remains pending.

## Verification summary

| Gate | Status | Evidence |
|---|---|---|
| Linux build | Foundation passed | 10 projects, 0 warnings, 0 errors |
| Tests | Foundation passed | 25 passed, 0 failed |
| Linux demo | Pending | — |
| win-x64 publish | Pending | — |
| Windows VM smoke | Pending | — |
| Dependency/license audit | Pending | — |
| Safety/secret audit | Pending | — |
| Claude reviews | Pending | — |

## Feature matrix

Populated after integration from the acceptance checklist. A feature is marked complete only with a deterministic test, simulator check, or Windows smoke assertion.

## Recognition results

Accuracy by resolution/context and measured scan latency are populated after fixture integration. Low-confidence exclusions count as explicit ambiguity, not false success.

## Performance

Startup, warm search, single scan, screenshot event latency, and memory measurements are populated from release builds.

## Data and licensing

The verified endpoint catalog and current versions are recorded in `docs/DATA_SOURCES.md`. The final dependency table and map-asset decisions are recorded in `docs/LICENSING.md` and `docs/THIRD_PARTY_NOTICES.md`.

## Safety audit

The release contains no game memory access, injection/hooks, packet inspection, input automation, enemy detection/tracking, in-game overlay, flea automation, telemetry, or screenshot upload. This statement is re-verified by source/dependency review at package time.

## Reviews and known limitations

Claude architecture, recognition, safety/license, and final review summaries are populated after each report is addressed. Real-game behavior, OCR calibration against live EFT visuals, and real screenshot/map transform plausibility remain pending until `docs/LIVE_EFT_VALIDATION.md` is completed.

## Required final statement

The v1 build is complete and has been validated through Linux fixture/demo tests and Windows VM simulator integration. Direct validation against Escape from Tarkov remains pending because EFT was not installed in the VM during the autonomous build.

The paragraph above is required release wording but is not yet an active completion claim while this report remains `Status: In progress`.
