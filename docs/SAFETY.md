# Safety and anti-cheat boundary

Tarkov Companion is a separate desktop application. Its integration boundary is external, visible, read-only, and user-triggered where capture is involved.

## Allowed evidence

- Visible pixels captured through ordinary Windows desktop/window APIs.
- Screenshots and logs written by Escape from Tarkov.
- Screenshot filenames containing position/orientation metadata.
- Ordinary process/window enumeration for finding the visible game window.
- Public APIs and locally cached public/curated data.
- Local user-entered profile, event, route, and strategy state.

## Other players' data in the game's own logs

Escape from Tarkov writes group notifications and inventory state into its own log files, and
those blobs contain real personal data for the player **and for anyone they grouped with**:
nicknames, numeric account and profile ids, levels, loadouts, health state, and looted dogtags
naming a killer and a victim.

What this data is determines whether it may be used, and the line is who the player has
already legitimately encountered:

- **Permitted.** The player's own profile, inventory and raid state. Their own party's
  composition, levels and loadouts, which the game already shows them in the raid. Dogtags the
  player has looted, whose killer and victim names are printed on the item they are carrying.
- **Prohibited.** Anything describing a player they have not encountered, anything that
  locates another player, and any aggregation across raids that would build a picture of
  somebody the player never met. That is the enemy-tracking boundary below, and party members
  are not enemies.

Three rules apply whenever this data is read:

1. **Never transmitted.** It is read locally, displayed locally, and never uploaded, bundled,
   attached to a diagnostic report, or written into an exported file.
2. **Never attributed wrongly.** Another player's record is never displayed or stored as
   though it were the player's own. Where the source cannot distinguish the two, neither is
   shown.
3. **Opened deliberately.** Log files carrying this data are read only when a feature needs
   them, never incidentally. `application`, `output` and `backend` are read in full (`backend`
   for the exact raid start and end and the player's own party); `push-notifications` only for
   lines carrying a quest or flea marker, which go to those two parsers and nothing else. The
   other files, `inventory` and `player` among them, are not opened (`EftLogFiles`). A flea
   payment message names the buyer, a player never met, and that field is never read.

## Prohibited implementation

Three anti-cheat fixtures are immutable: the project never reads or writes Escape from Tarkov
process memory, never generates game-directed mouse, keyboard, or controller input, and never
renders an in-game overlay.

The current design additionally excludes injection and game/renderer hooks, driver/kernel
inspection, inspection or decoding of EFT network packets, flea/inventory automation, live
enemy detection or tracking, ESP/radar, and aiming/combat assistance. These are explicit design
exclusions even where they overlap the three immutable fixtures.

Historical or predicted traffic is computed only from static spawns, points of interest,
chokepoints, public map topology, extracts, elapsed raid phase, and bounded historical evidence.
Routine presentation carries the compact category identity `Historical`, `Modelled`, or
`Predicted`, never a live-detection or current-location claim. Freshness and confidence are inline
when either could materially change a decision. Full source, observed/data-through/generated UTC
times, coverage, confidence and calibration, and model version remain available on demand in
`Why` or details and in the version-matched Setup/Admin `Data & Privacy` explanation; the stored
evidence and its validation are unchanged by progressive disclosure. Active consent and sharing
state, security state, destructive effects, failures, and decision-changing uncertainty remain
visible rather than being deferred to details.

## Enforcement

- Permanent rules live in root `AGENTS.md`.
- The normative v2 evidence and protocol rules live in `docs/V2_CONTRACT.md` and ADR 0008.
- Platform interfaces expose capture and observation, never process handles for memory operations or input sending.
- Dependency/source audits search for known injection, hooking, packet-capture, automation, and memory-access packages/APIs.
- Deterministic tests assert that strategy inputs have no live-enemy concept.
- Captures are never retained by default. The shareable desktop support bundle is a closed projection that excludes tokens, paths, names, coordinates, screenshots/OCR, logs, and free-form details; the relay must independently enforce that schema before the end-to-end report risk can close. Self-test output is local diagnostic data and may contain local paths.
- No telemetry or screenshot upload SDK is included.
- The optional TarkovTracker adapter exposes only canonical HTTPS `GET /token`
  and `GET /progress`; redirects, team access, HTTP mutation, uploads, and
  automatic apply are absent. Composition and status checks are offline, and a
  fetched snapshot cannot mutate local progress before reviewed confirmation.
