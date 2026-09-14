# Anti-cheat misuse case review

Each immutable boundary from `docs/SAFETY.md` / `AGENTS.md`, checked against the three
independent things `METHODOLOGY.md` names: the pattern-level static control, the architectural
boundary, and what this pass actually read. **This reviews the current, real architecture — it
does not restate the policy.**

## 1. No EFT process memory access

- **Pattern-level control:** `scripts/audit-safety.sh` greps `src/` for
  `OpenProcess|ReadProcessMemory|WriteProcessMemory|VirtualAllocEx|CreateRemoteThread|NtQueryVirtualMemory`
  and fails the build on a match. Runs in `ci.yml` and `windows-verify.yml` on every push —
  **Tested (automated)**, verified by reading both workflow files.
- **Architectural boundary:** `docs/ARCHITECTURE.md` confines all Windows-specific code to
  `Platform.Windows`, behind interfaces `Core` and `Application` consume without knowing they are
  platform-specific. Process/window enumeration exists (`Platform.Windows/Discovery`) but is
  documented and, per file inspection, scoped to finding the visible game window and its handle
  for *capture* — not for opening it with memory-read access rights.
- **What was read:** `src/TarkovCompanion.Platform.Windows/Discovery/` and `Capture/` directory
  listings, `docs/ARCHITECTURE.md`, `docs/SAFETY.md`. No `OpenProcess`/`ReadProcessMemory` call
  exists in `src/` as of this pass (consistent with the passing `audit-safety.sh` pattern, which
  this pass did not re-run but whose pattern was read and confirmed to cover the right API names).
- **Verdict:** Held, on both pattern and architecture grounds.

## 2. No injection or hooks

- **Pattern-level control:** Same script, same run, additional patterns:
  `SetWindowsHookEx|EasyHook|Reloaded\.Hooks|MemorySharp|GameOverlay|Vortice\.Direct3D.*Hook`, plus
  a second check against `.csproj`/`.props`/`.targets` for package references to
  `EasyHook`/`MemorySharp`/`GameOverlay` — **Tested (automated)**.
- **Architectural boundary:** No renderer, DLL-injection, or driver project exists in the
  solution (`TarkovCompanion.sln` project list: App, Application, Core, EftSimulator,
  GroupServer, Infrastructure, Platform.Windows, Platform.Windows.Ocr). Capture is
  screen/window-level (`Platform.Windows/Capture`), which is the ordinary desktop API surface
  `docs/SAFETY.md` names as allowed evidence.
- **What was read:** Solution project list, `docs/SAFETY.md` allowed-evidence section,
  `Platform.Windows/Capture` directory listing.
- **Verdict:** Held.

## 3. No EFT traffic inspection or decoding

- **Pattern-level control:** `WinDivert|SharpPcap|PacketDotNet` in the source grep, plus the same
  three names in the `.csproj`/`.props` package-reference check — **Tested (automated)**.
- **Architectural boundary:** `Directory.Packages.props` (read in full this pass) lists no
  packet-capture package. The project's only network code is ordinary `HttpClient` usage
  (`Infrastructure`, `GroupServer`) against named HTTPS APIs, not a raw-socket or capture-driver
  dependency.
- **What was read:** `Directory.Packages.props` in full, `docs/SAFETY.md` prohibited-implementation
  list.
- **Verdict:** Held. Note: this boundary is about *game* traffic specifically — the group relay's
  own HTTP protocol (`docs/GROUP_RELAY.md`) is the project's own invented wire format between its
  own components, not EFT's, and reviewing *that* protocol's abuse surface is what
  `ABUSE_CASES.md`'s TB-4/TB-6/TB-8 sections do. It is a different question from this boundary.

## 4. No game-directed input synthesis

- **Pattern-level control:** `SendInput|mouse_event|keybd_event|GetAsyncKeyState` in the source
  grep — **Tested (automated)**.
- **Architectural boundary:** `docs/ARCHITECTURE.md` states the application "owned a global
  hotkey and no longer does: the window's own key bindings fire only when the companion has
  focus, which is the correct behaviour beside a fullscreen game" — i.e., the one place input
  handling used to reach toward the game was deliberately removed. Confirmed no
  `Platform.Windows` file references `SendInput` or equivalent by directory listing plus the
  passing grep pattern.
- **What was read:** `docs/ARCHITECTURE.md` runtime-flow section, `Platform.Windows` directory
  listing.
- **Verdict:** Held, and the architecture note itself is evidence of a prior tightening, not just
  an absence.

## 5. No flea/inventory/aiming/combat automation

- **Pattern-level control:** Not a named API pattern (automation here would be *this project's
  own logic*, not a recognizable Windows/hooking API), so `audit-safety.sh` cannot catch a
  violation of this boundary directly — it can only catch the *mechanism* (input synthesis, #4)
  such automation would need to act through.
- **Architectural boundary:** The strategy/recommendation engine (`docs/STRATEGY.md`,
  `docs/ARCHITECTURE.md`: "The strategy engine consumes public/static map inputs and never
  consumes enemy observations") produces advisory output for the player to act on, not commands
  sent anywhere. No code path in `Application`/`Infrastructure` writes back toward the game
  process or its files in a way that could constitute automation — the desktop's only writes
  toward the game's own folders are moving stale screenshots to the recycle bin, and only when
  Debug Capture / screenshot cleanup is explicitly enabled (`docs/SAFETY.md`).
- **What was read:** `docs/STRATEGY.md`, `docs/ARCHITECTURE.md`, `docs/SAFETY.md` allowed/prohibited
  sections.
- **Verdict:** Held by design (advisory-only output), but **this is the boundary with the weakest
  automated backstop** — it depends on no future feature adding a write-back path, which
  `audit-safety.sh` would not catch unless that path also happened to use one of the named
  patterns. Recorded as a standing review obligation for any future PR touching `Application`'s
  use-case layer, not a current gap.

## 6. No live enemy tracking, ESP, or radar

- **Pattern-level control:** Not a named API pattern — this is a data-modeling boundary, not an
  API-call boundary.
- **Architectural boundary:** `docs/SAFETY.md`'s "Other players' data in the game's own logs"
  section draws the line explicitly: party members already encountered are permitted, anyone
  never encountered and any cross-raid aggregation about them is prohibited. `docs/ARCHITECTURE.md`
  confirms the strategy engine "never consumes enemy observations," and the group relay's own
  design deliberately prunes anyone outside the room "on the way in and on the way out" because a
  matchmade five-man carries strangers (`docs/ARCHITECTURE.md`, "The group relay" section).
- **What was read:** `docs/SAFETY.md` in full, `docs/ARCHITECTURE.md`'s group-relay section,
  `GroupRooms`/`GroupContracts.cs` directory listing (confirmed no per-enemy or cross-raid
  aggregation type exists in the relay's contract surface).
- **Verdict:** Held. This is a data-governance boundary rather than a code-pattern one, so its
  ongoing enforcement depends on review discipline at each future feature (#305 health/character
  recognition, #311 historical-traffic datasets) rather than a static grep — flagged explicitly in
  `TBD_COMPONENTS.md` for both.

## 7. No in-game overlay

- **Pattern-level control:** `SetWindowPos|WS_EX_TOPMOST` in the source grep — a topmost,
  positioned window is the mechanism an overlay would need — **Tested (automated)**.
- **Architectural boundary:** `README.md` states plainly there is no overlay; the application is
  a second-screen companion. No `App/Views` file was found with topmost/overlay styling by
  directory listing.
- **What was read:** `README.md`, `src/TarkovCompanion.App/Views` directory listing.
- **Verdict:** Held.

## 8. Historical/modelled intelligence carries source, timestamps, coverage, confidence, model version — and is never presented as live

- **Pattern-level control:** None — this is a data-contract boundary, enforced (per #264, not yet
  merged to `main` as of this pass) by an evidence/provenance envelope type, not a grep pattern.
- **Architectural boundary:** `docs/SAFETY.md` states the rule ("Predicted traffic is computed
  only from static spawns... Every presentation must label it as predicted educational guidance,
  not live player data"). `AGENTS.md` rules 18–19 restate it as permanent project rules. The
  concrete enforcement mechanism — a typed evidence envelope with source class,
  observed/data-through/generated UTC, confidence, and coverage/sample size, carried on every
  result — is the subject of #264's contract freeze, which had not landed on `main` at the time of
  this pass (`docs/V2_CONTRACT.md` does not exist yet — confirmed by file check).
- **What was read:** `docs/SAFETY.md`, `AGENTS.md`, `#264`'s issue body (for what the envelope is
  meant to contain), a direct check that `docs/V2_CONTRACT.md` does not exist on `main`.
- **Verdict:** **Held as a stated policy, not yet held as an enforced contract.** This is the one
  boundary in this list where the review's honest verdict is "not fully verifiable yet" rather
  than "held" — recorded as a TBD dependency in `TBD_COMPONENTS.md` rather than asserted as
  closed. Once #264 merges, this section should be re-reviewed against the actual envelope type
  (are predictions structurally prevented from serializing with a live-detection evidence class,
  per #264's own acceptance criterion, or only prevented by convention?).

## Design pattern summary

| Boundary | Enforced by pattern grep? | Enforced by architecture? | Enforced by data contract? |
| --- | --- | --- | --- |
| 1. Memory access | Yes | Yes | — |
| 2. Injection/hooks | Yes | Yes | — |
| 3. Traffic inspection | Yes | Yes | — |
| 4. Input synthesis | Yes | Yes | — |
| 5. Automation | No | Yes (advisory-only output) | — |
| 6. Enemy tracking/ESP | No | Yes | Partial (relay pruning; #305/#311 pending) |
| 7. Overlay | Yes | Yes | — |
| 8. Honest modelled intelligence | No | Policy only | **Pending #264** |

Boundaries 5, 6, and 8 are the ones with no automated backstop today and depend on review
discipline at each future feature. That is a finding in itself, recorded as
RISK-ANTICHEAT-REVIEW-DISCIPLINE in `CONTROLS_AND_RESIDUAL_RISK.md`.
