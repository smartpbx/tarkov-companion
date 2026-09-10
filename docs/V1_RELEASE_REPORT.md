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
| Linux build | Passed | Serial Release build of 10 projects: 0 warnings, 0 errors |
| Tests | Passed | 225 passed, 0 failed, 5 skipped (the Windows/native rendered-pixel OCR cases remain environment-gated) |
| Linux demo | Pending | — |
| win-x64 publish | Passed | Clean self-contained staging produced a 300-entry archive with project/license policy, notices, locked inventory, and 23 license/notice files; no runtime map cache/artwork entries |
| Windows VM smoke | Pending | — |
| Dependency/license audit | Passed | Fail-closed local audit: 52 runtime, 1 build-only, 6 non-Windows, and 13 test-only packages plus 13 bundled components; missing-mapping fixture rejected as required |
| Safety/secret audit | Passed | `scripts/audit-safety.sh` and `scripts/scan-secrets.sh` both passed on the release-licensing validation host |
| Claude reviews | In progress | Independent safety/license review completed; its release-blocking third-party notice finding is remediated by the locked inventory and packaged `LICENSES/` corpus |

## Feature matrix

Populated after integration from the acceptance checklist. A feature is marked complete only with a deterministic test, simulator check, or Windows smoke assertion.

## Recognition results

Accuracy by resolution/context and measured scan latency are populated after fixture integration. Low-confidence exclusions count as explicit ambiguity, not false success.

## Performance

Startup, warm search, single scan, screenshot event latency, and memory measurements are populated from release builds.

## Data and licensing

The verified endpoint catalog and current versions are recorded in `docs/DATA_SOURCES.md`. The final package graph and bundled native/font/data components are locked with NuGet content hashes in `docs/THIRD_PARTY_INVENTORY.json`; policy, authoritative evidence, notices, and exact redistributed texts are recorded in `docs/LICENSING.md`, `docs/THIRD_PARTY_NOTICES.md`, and `LICENSES/`. Optional tarkov.dev map artwork remains runtime-cached, visibly attributed, and absent from the source and release archive.

## Safety audit

The release contains no game memory access, injection/hooks, packet inspection, input automation, enemy detection/tracking, in-game overlay, flea automation, telemetry, or screenshot upload. This statement is re-verified by source/dependency review at package time.

## Reviews and known limitations

Claude architecture, recognition, safety/license, and final review summaries are populated after each report is addressed. The TesseractOCR NuGet package does not publish a native dependency manifest or NOTICE; its packaged binaries were therefore documented from package metadata, the recorded repository commit, PE imports, and embedded version/copyright strings, with exact upstream license texts shipped for each identified static codec. Real-game behavior, OCR calibration against live EFT visuals, real screenshot/map transform plausibility, and Windows VM smoke remain pending until their dedicated validation is completed.

## Required final statement

The v1 build is complete and has been validated through Linux fixture/demo tests and Windows VM simulator integration. Direct validation against Escape from Tarkov remains pending because EFT was not installed in the VM during the autonomous build.

The paragraph above is required release wording but is not yet an active completion claim while this report remains `Status: In progress`.
