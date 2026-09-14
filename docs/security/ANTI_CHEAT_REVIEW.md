# Anti-cheat misuse-case review

This reviews current source against the three immutable fixtures, the additional current design
exclusions, and the evidence/presentation contract in `docs/SAFETY.md`, `AGENTS.md`, and
`docs/V2_CONTRACT.md`.
A **Held (current source)** verdict means the reviewed implementation contains no observed
violation. It does not mean a directory layout or lexical grep makes a future violation
impossible. `scripts/audit-safety.sh` is recorded as **Reviewed** and configured in CI; a specific
passing CI run is separate automated evidence and proves only what its named patterns can detect.

## 1. No EFT process memory access

- **Pattern control:** `audit-safety.sh` names `ReadProcessMemory`, `WriteProcessMemory`,
  `VirtualAllocEx`, `VirtualProtectEx`, `CreateRemoteThread`, and the relevant `Nt*Memory` APIs.
  Query-only `OpenProcess` is intentionally allowed for ordinary window discovery. The denylist
  catches literal names, not dynamically resolved APIs, differently named libraries, or an
  unlisted mechanism.
- **Current architecture/source:** `WindowsGameWindowLocator` enumerates processes and reads the
  visible main-window handle. `GdiScreenCaptureService` uses that handle with display/device-
  context APIs to copy visible pixels; it does not open the EFT process or read its memory.
- **Reviewed:** `WindowsGameWindowLocator.cs`, `GdiScreenCaptureService.cs`, project/package files,
  `scripts/audit-safety.sh`.
- **Verdict:** **Held (current source); partial lexical backstop.**

## 2. No injection, renderer hooks, or driver inspection

- **Pattern control:** the audit names `SetWindowsHookEx`, `EasyHook`, `Reloaded.Hooks`,
  `MemorySharp`, `GameOverlay`, and a Direct3D-hook-shaped Vortice pattern, and checks several
  package-reference names. It is a denylist, not a general proof of absence.
- **Current architecture/source:** the solution contains desktop, domain/application,
  infrastructure, simulator, relay, and ordinary Windows integration projects. The capture path
  is external GDI visible-pixel capture; no injection/renderer/driver integration was found in
  current source or package declarations.
- **Reviewed:** solution/project manifests, `Directory.Packages.props`,
  `GdiScreenCaptureService.cs`, `scripts/audit-safety.sh`.
- **Verdict:** **Held (current source); partial lexical/package backstop.**

## 3. No EFT traffic inspection or protocol decoding

- **Pattern control:** the audit names `WinDivert`, `SharpPcap`, and `PacketDotNet` in source and
  package references. It cannot prove that every possible BCL/native socket mechanism is absent.
- **Current architecture/source:** current network code is application-owned HTTP to named public
  APIs and the project's own group relay. No packet-capture dependency or EFT protocol decoder was
  found. Reviewing the companion's own relay JSON is not inspection of EFT traffic.
- **Reviewed:** `Directory.Packages.props`, Infrastructure HTTP clients, GroupServer contracts,
  `scripts/audit-safety.sh`.
- **Verdict:** **Held (current source); partial lexical/package backstop.**

## 4. No game-directed input synthesis

- **Pattern control:** the audit names `SendInput`, `mouse_event`, `keybd_event`, InputSimulator,
  WindowsInput, ViGEm, and vJoy. Physical-state observation such as `GetAsyncKeyState` is
  intentionally allowed; it is not input synthesis. The denylist cannot identify every renamed
  or dynamically resolved mechanism.
- **Current architecture/source:** companion commands are ordinary focused Avalonia bindings. No
  current platform interface or implementation sends keyboard, mouse, or controller input to EFT.
- **Reviewed:** App views/bindings, Platform.Windows source, `scripts/audit-safety.sh`.
- **Verdict:** **Held (current source); partial lexical backstop.**

## 5. No flea, inventory, aiming, or combat automation

- **Pattern control:** no generic grep can decide whether application logic is automation. The
  input-synthesis patterns catch some mechanisms an implementation might use, not intent or every
  output path.
- **Current architecture/source:** recommendation and strategy services return advisory data for
  the player. No product interface writes actions into EFT. Screenshot cleanup affects
  the user's screenshot files, not gameplay state. That cleanup currently defaults to enabled;
  it is a separate file-retention risk and does not persist the GDI capture bytes.
- **Reviewed:** recommendation/strategy contracts, application service interfaces,
  `docs/ARCHITECTURE.md`, `docs/STRATEGY.md`.
- **Verdict:** **Held (current source); enforced by product architecture and review, not an
  automated semantic control.**

## 6. No live enemy tracking, ESP, or radar

- **Pattern control:** none; this is an evidence/data-model/UI boundary.
- **Current architecture/source:** the strategy engine consumes static/public zones, elapsed raid
  time, raid duration, and the player's own last-known screenshot-derived position to classify
  current-area risk; it has no live-enemy observation contract. Group state describes members of
  the voluntarily shared room, not detected enemies. Historical/modelled traffic remains allowed
  only when it is sourced, timestamped, covered, confidence-bearing, versioned, and presented as
  non-live.
- **Separate current conflict:** `GroupSessionService` transmits a pruned subset of party-member
  log data. That is not live enemy tracking, but it conflicts with the current `docs/SAFETY.md`
  prohibition on transmitting other players' log-derived data and is OPEN as
  RISK-RELAY-OBSERVED-DATA-POLICY. This review does not reinterpret that rule away.
- **Reviewed:** strategy/domain contracts, `GroupSessionService.cs`, `GroupContracts.cs`,
  `GroupRooms.cs`, `docs/SAFETY.md`.
- **Verdict:** **Held for the no-live-enemy boundary in current source; the generic V2 evidence
  contract rejects screenshot provenance, live/player vocabulary, and non-allowlisted payload
  shapes. It cannot decide whether semantically live observations were hidden inside an allowed
  log or curated input, so feature-specific #305/#311 assertions remain. A separate safety-policy/
  source mismatch remains open.**

## 7. No in-game overlay

- **Pattern control:** `audit-safety.sh` rejects `GameOverlay` and click-through windows, detects a
  topmost+layered Win32 flag combination across formatting, and detects game-window reparenting
  within one or adjacent statements. It fails closed on scanner errors and scan-root symlinks and
  self-tests symlink rejection. A pure Avalonia `Topmost` window is intentionally not forbidden
  because ordinary companion placement is allowed; a different or dynamically expressed overlay
  mechanism can still evade a lexical ratchet.
- **Current architecture/source:** current App views contain one normal main window and page/user
  controls. A source search found no `Topmost`, transparency/layered, click-through, reparenting,
  or overlay-specific window code. The product is used beside the game as a separate desktop
  window.
- **Reviewed:** all files under `src/TarkovCompanion.App/Views`, App startup/window code,
  Platform.Windows source, `scripts/audit-safety.sh`.
- **Verdict:** **Held (current source); only partial automated coverage, and the architecture does
  not make a future overlay impossible.** Every new window/placement feature requires explicit
  review against this hard boundary.

## 8. Historical/modelled intelligence retains evidence and is never presented as live

- **Pattern control:** none; this requires a data contract plus presentation tests.
- **Current policy:** `docs/SAFETY.md` and `docs/V2_CONTRACT.md` require compact Historical,
  Modelled, or Predicted identity; decision-material freshness/confidence inline; complete source,
  time, coverage, calibration, and version evidence on demand; and no live-detection/current-
  location claim. Precision must not imply a live observation.
- **Current structural status:** merged #264 supplies closed typed evidence/intelligence envelopes,
  source-class allowlists, bounded lineage, construction/JSON validation, and deterministic tests
  that reject screenshot provenance, live/player vocabulary, and non-allowlisted payload shapes.
  Allowed log or curated inputs can still conceal semantically live observations; the generic
  contract does not prove the future #305/#311 input validation, presenters, or dataset lifecycle
  are correct before they exist.
- **Reviewed:** `docs/SAFETY.md`, `docs/V2_CONTRACT.md`, ADR 0008, the merged Core V2 evidence and
  modelled-intelligence types, their wire allowlists/guards, and V2 contract tests.
- **Verdict:** **Held for the generic V2 evidence/wire contract; feature-specific review remains.**
  #305/#311 must add their own dataset, serialization, and presentation assertions.

## Enforcement summary

| Boundary | Lexical/package audit | Current architecture/source | Deterministic contract test |
| --- | --- | --- | --- |
| 1. Memory access | Partial denylist | Held | Not identified in this phase |
| 2. Injection/hooks | Partial denylist | Held | Not identified in this phase |
| 3. EFT traffic inspection | Partial denylist | Held | Not identified in this phase |
| 4. Input synthesis | Partial denylist | Held | Not identified in this phase |
| 5. Gameplay automation | Mechanism-only | Held, advisory-only | Needed for each new action surface |
| 6. Live enemy tracking/ESP | No semantic grep | Held; separate transmission conflict open | Generic V2 input contract landed; #305/#311 feature assertions pending |
| 7. In-game overlay | Statement-aware named-mechanism tripwires | Held today, not structurally impossible | Needed for new window/placement paths |
| 8. Honest modelled intelligence | None | Typed generic V2 contract landed | Generic contract tests landed; #305/#311 feature assertions pending |

The absence of a complete automated backstop is tracked as
RISK-ANTICHEAT-REVIEW-DISCIPLINE. Static patterns are useful ratchets, but no passing grep may be
used as a blanket anti-cheat certification.
