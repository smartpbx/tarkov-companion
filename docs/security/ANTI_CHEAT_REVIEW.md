# Anti-cheat misuse-case review

This reviews current source against the immutable boundaries in `docs/SAFETY.md` and `AGENTS.md`.
A **Held (current source)** verdict means the reviewed implementation contains no observed
violation. It does not mean a directory layout or lexical grep makes a future violation
impossible. `scripts/audit-safety.sh` is recorded as **Reviewed** and configured in CI; a specific
passing CI run is separate automated evidence and proves only what its named patterns can detect.

## 1. No EFT process memory access

- **Pattern control:** `audit-safety.sh` names `OpenProcess`, `ReadProcessMemory`,
  `WriteProcessMemory`, `VirtualAllocEx`, `CreateRemoteThread`, and `NtQueryVirtualMemory`.
  This catches literal uses of those names, not dynamically resolved APIs, differently named
  libraries, or an unlisted mechanism.
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

- **Pattern control:** the audit names `SendInput`, `mouse_event`, and `keybd_event`. It also bans
  `GetAsyncKeyState`, which observes key state rather than synthesizing it; that extra ban is
  defense-in-depth but is not evidence for the synthesis claim.
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
- **Current architecture/source:** the strategy engine consumes static/public inputs and has no
  live-enemy observation contract. Group state describes members of the voluntarily shared room,
  not detected enemies. Historical/modelled traffic remains allowed only when it is sourced,
  timestamped, covered, confidence-bearing, versioned, and presented as non-live.
- **Separate current conflict:** `GroupSessionService` transmits a pruned subset of party-member
  log data. That is not live enemy tracking, but it conflicts with the current `docs/SAFETY.md`
  prohibition on transmitting other players' log-derived data and is OPEN as
  RISK-RELAY-OBSERVED-DATA-POLICY. This review does not reinterpret that rule away.
- **Reviewed:** strategy/domain contracts, `GroupSessionService.cs`, `GroupContracts.cs`,
  `GroupRooms.cs`, `docs/SAFETY.md`.
- **Verdict:** **Held for the no-live-enemy boundary in current source; no automated semantic
  backstop. A separate safety-policy/source mismatch remains open.**

## 7. No in-game overlay

- **Pattern control:** `audit-safety.sh` names only `SetWindowPos` and `WS_EX_TOPMOST`. An Avalonia
  `Topmost` property, layered/click-through window, owner reparenting, or another mechanism need
  not contain either token. Conversely, ordinary topmost desktop UI would not alone prove an
  in-game overlay. The pattern is a warning tripwire, not structural enforcement.
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
- **Current policy:** `docs/SAFETY.md` and `AGENTS.md` require source, observed/data-through/
  generated UTC, coverage/sample size, confidence, model version, and unambiguous non-live
  presentation. Precision must not imply a live observation.
- **Current structural status:** the proposed common evidence/provenance envelope belongs to #264
  and was not present at baseline commit `76b506f`. Existing feature-specific provenance does not
  prove every future #305/#311 result is incapable of serializing or rendering as live.
- **Reviewed:** current domain/evidence types, `docs/SAFETY.md`, `AGENTS.md`, #264 acceptance text.
- **Verdict:** **Held as normative policy, not yet as a complete enforced contract.** #264/#305/
  #311 must add deterministic serialization and UI assertions before this can be upgraded.

## Enforcement summary

| Boundary | Lexical/package audit | Current architecture/source | Deterministic contract test |
| --- | --- | --- | --- |
| 1. Memory access | Partial denylist | Held | Not identified in this phase |
| 2. Injection/hooks | Partial denylist | Held | Not identified in this phase |
| 3. EFT traffic inspection | Partial denylist | Held | Not identified in this phase |
| 4. Input synthesis | Partial denylist | Held | Not identified in this phase |
| 5. Gameplay automation | Mechanism-only | Held, advisory-only | Needed for each new action surface |
| 6. Live enemy tracking/ESP | None | Held; separate transmission conflict open | Needed for #305/#311 |
| 7. In-game overlay | Partial two-token tripwire | Held today, not structurally impossible | Needed for new window/placement paths |
| 8. Honest modelled intelligence | None | Policy only | Pending #264/#305/#311 |

The absence of a complete automated backstop is tracked as
RISK-ANTICHEAT-REVIEW-DISCIPLINE. Static patterns are useful ratchets, but no passing grep may be
used as a blanket anti-cheat certification.
