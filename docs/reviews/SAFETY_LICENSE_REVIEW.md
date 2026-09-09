# Safety and license review — origin/main @ `20786ca`

- Reviewed commit: `20786cad2a5246008be51fa7a3e53e094be3d6a8` (origin/main, 2026-09-09)
- Reviewer: Claude safety/license review worker (task `task_437ae70f9db7`)
- Scope: full source, project/dependency graph, Windows/diagnostic APIs, strategy claims, fixtures, scripts, packaging, docs, git history
- Method: complete read of `src/TarkovCompanion.Platform.Windows` (all 8 files, 989 lines), pattern sweeps of the whole tree for prohibited APIs, `dotnet list package --include-transitive` inventory at this commit, repo secret scans (including the paths the repo's own scanner excludes), full-history secret scan of all 25 commits, and doc-versus-code claim verification.

## Executive summary

**The permanent safety boundary holds at this commit.** Every prohibited capability class — game memory access, injection/hooks, packet capture, gameplay input synthesis, live enemy tracking/ESP, in-game overlay, flea automation, telemetry, screenshot upload — is absent from the source, the dependency graph, and the git history. The entire native surface is ordinary user32/gdi32/shcore/kernel32/crypt32 observation APIs plus one read-only registry key. The only outbound network endpoint in the codebase is `https://json.tarkov.dev/`. No secrets exist in the tree or in any commit.

**Licensing is compatible but not yet release-complete.** All distributed dependencies are permissive (MIT / Apache-2.0 / BSD-3-Clause natives / OFL-1.1 font / public-domain SQLite); no copyleft is linked. However, `docs/THIRD_PARTY_NOTICES.md` is missing several components that a win-x64 publish actually ships (Inter font, SQLitePCLRaw/SQLite, HarfBuzz, ANGLE, native Skia, and others), and no verbatim license texts ship, while `scripts/package-windows.sh` will happily produce a `v1.0.0` zip carrying the incomplete notices. The repo's own release rule (`docs/LICENSING.md`) already declares this state release-blocking, so the gate below is marked FAIL for release-readiness, not as a policy violation of the current unreleased state. CC BY-NC-SA map artwork is neither bundled nor fetched at this commit, and the ADR 0002 posture toward it is sound.

No High-severity findings. One Medium (notices completeness vs. packaging), three Low (a SAFETY.md redaction claim with no implementation, secret-scan exclusions, safety-audit script fragility), and several Informational items.

## Pass/fail gates

| # | Gate | Verdict | Evidence summary |
|---|------|---------|------------------|
| G1 | No game memory access | **PASS** | No `OpenProcess`/`ReadProcessMemory`/`WriteProcessMemory`/`NtQuery*`/`VirtualAllocEx` anywhere. Window discovery uses only `Process.GetProcesses()` metadata + `GetWindowRect`/`IsIconic` (`src/TarkovCompanion.Platform.Windows/Discovery/WindowsGameWindowLocator.cs:65-107`). No process handle is ever opened with memory rights. |
| G2 | No injection / renderer hooks | **PASS** | No `SetWindowsHookEx`, `CreateRemoteThread`, `LoadLibrary`/`GetProcAddress`, Detours/MinHook/EasyHook, or D3D/DXGI references in any file. All 28 `LibraryImport` declarations enumerated (appendix) — none touches another process. |
| G3 | No packet capture / traffic decoding | **PASS** | No pcap/Npcap/SharpPcap/WinDivert/raw-socket usage. The only network client is `HttpClient` against `https://json.tarkov.dev/` (`src/TarkovCompanion.Infrastructure/TarkovDevJson/TarkovDevJsonClientOptions.cs:5`), with ETag/If-Modified-Since conditional requests (`TarkovDevJsonClient.cs:356-364`). |
| G4 | No gameplay input generation | **PASS** | No `SendInput`/`keybd_event`/`mouse_event`/`SendMessage`-to-foreign-window. `RegisterHotKey` only *receives* WM_HOTKEY (`Hotkeys/WindowsGlobalHotkeyService.cs:82-95`); `PostThreadMessage` posts WM_QUIT only to the service's own pump thread (`:63`). |
| G5 | No live enemy tracking / ESP / radar | **PASS** | Zero matches for enemy/ESP/radar concepts across `src/` and `tests/`. Strategy inputs are static authored zone weights + elapsed raid phase + the user's own last-known screenshot position (`src/TarkovCompanion.Core/Domain/Strategy/Strategy.cs:13-33`). The non-live disclaimer is the *default value* of the domain record (`Strategy.cs:33`), re-asserted by `StrategyModel.NonLiveDisclaimer`, surfaced in the UI (`src/TarkovCompanion.App/Views/Pages/RaidView.axaml:135`), and locked by tests (`tests/TarkovCompanion.UnitTests/StrategyServicesTests.cs:27,50,52`). |
| G6 | No in-game overlay | **PASS** | No `Topmost`, `WS_EX_TOPMOST/LAYERED/TRANSPARENT`, or `SetWindowPos` anywhere. App manifest requests `asInvoker` with `uiAccess="false"` (`src/TarkovCompanion.App/app.manifest`). Second-screen Avalonia windows only. |
| G7 | No flea-market automation | **PASS** | `FleaListingParser` parses OCR text rows from user-triggered captures only (`src/TarkovCompanion.Infrastructure/Recognition/FleaListingParser.cs`); no clicking, typing, or purchase paths exist (no input APIs per G4, no `Process.Start` in `src/`). Recommendation engine output is advisory records only. |
| G8 | No telemetry / screenshot upload | **PASS** | Single outbound host (G3); no analytics/crash-reporting SDKs in the transitive graph (appendix). `GdiScreenCaptureService` keeps captured bytes in memory and never persists them (`Capture/GdiScreenCaptureService.cs:9,118`); no upload code exists. See Info I2 for a build-time (dev-machine-only) caveat on `Avalonia.BuildServices`. |
| G9 | No leaked secrets | **PASS** | Repo-wide pattern scan including `fixtures/` and `docs/` (which `scripts/scan-secrets.sh` excludes): clean. Full-history scan of all 25 commits: the only matches are the scanner's own regex. DPAPI CurrentUser store handles future tokens (`Security/WindowsDpapiSecretStore.cs`); `.gitignore` excludes DBs, captures, and support output. |
| G10 | NuGet license compatibility with MIT release | **PASS** | Every distributed package is MIT, Apache-2.0, BSD-3-Clause (native ANGLE/Skia), OFL-1.1 (Inter font), or public domain (SQLite). No GPL/LGPL/copyleft is compiled or linked. TarkovMonitor (GPL-3.0) and RatScanner are documented no-copy references only (`docs/LICENSING.md:9-10`); no evidence of copied source. |
| G11 | Third-party notices complete for the shipped artifact | **FAIL** (release-blocking, acknowledged) | `docs/THIRD_PARTY_NOTICES.md` omits ~7 shipped components and no verbatim license texts ship, while `scripts/package-windows.sh:40-47` already produces a `v1.0.0` zip carrying that file. See finding M1. The repo's own rule (`docs/LICENSING.md:15`) blocks release until fixed. |
| G12 | tarkov.dev / API attribution | **PASS** | Attribution in `README.md:33-37`, `docs/THIRD_PARTY_NOTICES.md:5-7`, and `docs/DATA_SOURCES.md`. Non-affiliation disclaimer for Battlestate Games present (`README.md:37`). |
| G13 | CC BY-NC-SA map assets not bundled / not commercially distributed | **PASS** | No SVG/PNG/image files exist anywhere in the repo (verified by extension sweep); no map-asset download code exists on main yet. ADR 0002 commits to reference-not-embed, per-asset attribution/license/hash records, and a written review before any bundled or commercial use (`docs/adr/0002-third-party-map-assets.md`). The pending map task (`.agents/tasks/tarkovdev-map-assets.md`) explicitly requires visible attribution, CC BY-NC-SA 4.0 documentation, and the upstream anti-cheat restriction. |
| G14 | Documentation claims match implementation | **PASS with exceptions** | Strategy, recognition, Windows, product, and testing docs accurately describe the code (spot-verified claim by claim). Exception: the SAFETY.md support-bundle redaction claim (finding L1). |

## Findings (severity-ranked)

### High

None.

### Medium

**M1 — THIRD_PARTY_NOTICES is incomplete relative to what `package-windows.sh` actually ships.**
The self-contained win-x64 publish distributes, beyond what the notices list: the **Inter typeface** (SIL OFL-1.1, embedded via `Avalonia.Fonts.Inter` and activated by `WithInterFont()` in `src/TarkovCompanion.App/Program.cs:57` — OFL requires its copyright notice and license text to accompany the font), **SQLitePCLRaw** (Apache-2.0) and the bundled **SQLite** engine (public domain) via `Microsoft.Data.Sqlite`, **HarfBuzzSharp/HarfBuzz** (MIT/“Old MIT”), **ANGLE** native binaries (BSD-3-Clause, via `Avalonia.Angle.Windows.Natives`), native **Skia** (BSD-3-Clause; the notices list SkiaSharp’s MIT wrapper only), plus `MicroCom.Runtime` and `Tmds.DBus.Protocol` (MIT). Additionally, no `LICENSES/` texts ship at all, though MIT/Apache-2.0/BSD/OFL all require reproducing their notices, and `docs/LICENSING.md:15` itself mandates shipping them. Meanwhile `scripts/package-windows.sh` (lines 40-47) copies the incomplete notices into a zip already labeled `TarkovCompanion-v1.0.0-win-x64.zip`.
*Impact:* an artifact produced today would under-attribute shipped components. *Status:* consistent with the docs’ own “completed before release” caveat (`docs/THIRD_PARTY_NOTICES.md:3`, `docs/V1_RELEASE_REPORT.md` gate “Dependency/license audit: Pending”), but it is the one gap that must close before any distribution. The full resolved inventory is in the appendix.

### Low

**L1 — SAFETY.md claims redaction that is not implemented.**
`docs/SAFETY.md:26` states “Support bundles redact tokens and user path segments; captures are opt-in only.” No support-bundle or redaction code exists anywhere in `src/` (zero matches for redact/support-bundle). The nearest artifact, the `--self-test` report, embeds unredacted absolute local paths — `appBase`, `currentDirectory`, and EFT root env values — which contain the Windows username (`src/TarkovCompanion.App/Services/Diagnostics/SelfTestRunner.cs:105-113`). If users are asked to share self-test JSON for support, usernames leak. Either implement redaction before shipping a support flow or soften the SAFETY.md claim to describe the current state.

**L2 — The CI secret scan excludes `fixtures/**` and `docs/**`.**
`scripts/scan-secrets.sh:8-9` carves both trees out of the gate. My manual scan of those paths (and of full history) is clean today, but fixtures are exactly where captured API responses or tokens tend to land accidentally. Recommend narrowing the exclusion to specific known-noisy files rather than whole trees.

**L3 — `audit-safety.sh` can pass vacuously and its blocklist has gaps.**
The script requires `rg`; if ripgrep is absent, `if rg …` sees exit 127, the branch is not taken, and the script prints “Safety audit passed” without having scanned anything (`scripts/audit-safety.sh:7-13`). GitHub’s ubuntu runners currently preinstall ripgrep, so CI is effectively covered today, but the failure mode is silent. Also, the forbidden pattern omits `OpenProcess`, `WinDivert`, `GetAsyncKeyState`, `SetWindowPos`/`WS_EX_TOPMOST` (overlay vector), and `Windows.Graphics.Capture` variants. A blocklist is inherently incomplete — fine as a tripwire — but add a `command -v rg` guard and the missing patterns.

### Informational

- **I1 — Anti-cheat posture.** The implementation avoids every vector anti-cheat systems monitor: it never opens a handle to the game process, never injects, never draws over the game window, and generates no input. That is the strongest defensible position an external companion can take; it is still not a guarantee against Battlestate Games’ terms, which disfavor third-party tools broadly. The README’s non-affiliation disclaimer and the “no overlay in v1” rule (AGENTS.md #8) are the right posture. The upstream tarkov-dev-svg-maps anti-cheat restriction becomes relevant only when the pending map-asset work lands; that task already requires documenting it.
- **I2 — `Avalonia.BuildServices` 11.3.2** is a build-time-only package that has historically collected anonymous build telemetry on the developer machine. It ships nothing into the app and does not affect the runtime “no telemetry” claim (G8). If dev-machine telemetry matters, verify current behavior and set the opt-out in CI.
- **I3 — Self-test safety flags are declarations, not measurements.** `SelfTestRunner.cs:114-120` hardcodes `readsGameMemory: false` etc. That is fine as a machine-readable statement of design, but it should not be presented as a runtime proof.
- **I4 — Fixtures are genuinely synthetic.** `fixtures/api/*` are hand-authored envelope exercises (fake IDs like `item-001`; `fixtures/api/README.md` states no copied production datasets); `fixtures/maps/training-ground.json` and `assets/strategy/generic-training-ground.json` are code-authored and non-selectable as production locations; curated/event example assets are `enabled: false` templates with no asserted game facts. One screenshot filename in `fixtures/screenshot-filenames/observed.txt` follows the real EFT format but contains only coordinates — no account identity.
- **I5 — Supply-chain hygiene is good.** SDK bootstrap pins an SHA-512 (`scripts/bootstrap.sh`), builds are `Deterministic`, CI runs `dotnet list package --vulnerable --include-transitive`, central package management pins all versions, no binaries are committed anywhere in history, and `dist/` contains only `.gitkeep`.
- **I6 — Diagnostic surface is minimal.** The developer diagnostic channel is file-based (no sockets/pipes), off by default, requires an explicit `--developer-mode` style opt-in plus a ≥32-char token compared in constant time, and accepts only `scan`/`scenario` identifiers (`src/TarkovCompanion.App/Services/Diagnostics/DiagnosticCommandChannel.cs`).
- **I7 — TarkovTracker integration is documentation-only at this commit.** No client code, token flow, or external write exists; DATA_SOURCES.md correctly scopes it as optional/read-only for the future.

## Appendix A — Complete native API surface (all 28 P/Invokes)

| DLL | Functions | Purpose |
|-----|-----------|---------|
| user32 | GetWindowDC, ReleaseDC, GetSystemMetrics, SetThreadDpiAwarenessContext, GetWindowRect, IsIconic, EnumDisplayMonitors, GetMonitorInfoW, RegisterHotKey, UnregisterHotKey, GetMessageW, PeekMessageW, PostThreadMessage | Visible-pixel capture DCs, window/monitor metadata, hotkey receipt |
| gdi32 | CreateCompatibleDC, CreateCompatibleBitmap, SelectObject, BitBlt, GetDIBits, DeleteObject, DeleteDC | Copy visible pixels to an in-memory BGRA buffer |
| shcore | GetDpiForMonitor | Per-monitor DPI |
| kernel32 | GetCurrentThreadId, LocalFree | Hotkey pump identity; DPAPI buffer release |
| crypt32 | CryptProtectData, CryptUnprotectData | DPAPI CurrentUser secret storage |

Plus one read-only registry read: `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\EscapeFromTarkov\InstallLocation` (`Discovery/WindowsEftPathLocator.cs:92-104`). Nothing else crosses a process boundary.

## Appendix B — Resolved dependency inventory at `20786ca` (src projects, distributed)

Generated via `scripts/audit-licenses.sh` (`dotnet list package --include-transitive`) at this commit.

| Component | Version | License |
|-----------|---------|---------|
| Avalonia (+ Desktop, Skia, Win32, X11, FreeDesktop[.AtSpi], Native, HarfBuzz, Themes.Fluent, Remote.Protocol, Fonts.Inter) | 12.1.2 | MIT |
| Avalonia.Angle.Windows.Natives | 2.1.27548.20260419 | MIT wrapper; bundles ANGLE (BSD-3-Clause) |
| Avalonia.BuildServices (build-time only, not shipped) | 11.3.2 | MIT |
| Inter typeface (embedded by Avalonia.Fonts.Inter) | — | SIL OFL-1.1 |
| CommunityToolkit.Mvvm | 8.4.2 | MIT |
| HarfBuzzSharp (+ NativeAssets Linux/macOS/WebAssembly/Win32) | 8.3.1.3 | MIT wrapper; HarfBuzz “Old MIT” |
| MicroCom.Runtime | 0.11.6 | MIT |
| Microsoft.Data.Sqlite (+ .Core) | 10.0.12 | MIT |
| Microsoft.Extensions.* (Configuration[.Json/.Binder/.FileExtensions/.Abstractions], DependencyInjection[.Abstractions], Diagnostics[.Abstractions], FileProviders[.Physical/.Abstractions], FileSystemGlobbing, Http, Logging[.Abstractions], Options[.ConfigurationExtensions], Primitives) | 10.0.12 | MIT |
| SkiaSharp (+ NativeAssets Linux/macOS/WebAssembly/Win32) | 3.119.4 | MIT wrapper; Skia BSD-3-Clause |
| SQLitePCLRaw (bundle_e_sqlite3, core, lib.e_sqlite3, provider.e_sqlite3) | 2.1.12 | Apache-2.0; SQLite public domain |
| Tmds.DBus.Protocol | 0.94.1 | MIT |
| .NET 10 runtime (self-contained publish) | 10.0.12 | MIT |

Test-only (not distributed): xunit 2.9.3 (Apache-2.0), xunit.runner.visualstudio 4.0.0, Microsoft.NET.Test.Sdk 18.10.0, Microsoft.TestPlatform.* 18.10.0, Microsoft.CodeCoverage 18.10.0, coverlet.collector 10.0.1 (MIT).

## Required actions before any release (from G11/M1)

1. Regenerate `docs/THIRD_PARTY_NOTICES.md` from the locked graph above, adding Inter (OFL-1.1), SQLitePCLRaw/SQLite, HarfBuzz(Sharp), ANGLE, native Skia, MicroCom.Runtime, and Tmds.DBus.Protocol.
2. Ship verbatim license texts (a `LICENSES/` directory beside the executable), including the full OFL-1.1 text for Inter, per `docs/LICENSING.md:15`.
3. Re-run `scripts/audit-licenses.sh` at the release commit and record the final table in the release report.
4. Optionally address L1–L3 (SAFETY.md redaction wording or implementation, secret-scan exclusions, audit-safety guard/patterns) — none blocks the safety boundary itself.
