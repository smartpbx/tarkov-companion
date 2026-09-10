# Safety and anti-cheat boundary

Tarkov Companion is a separate desktop application. Its integration boundary is external, visible, read-only, and user-triggered where capture is involved.

## Allowed evidence

- Visible pixels captured through ordinary Windows desktop/window APIs.
- Screenshots and logs written by Escape from Tarkov.
- Screenshot filenames containing position/orientation metadata.
- Ordinary process/window enumeration for finding the visible game window.
- Public APIs and locally cached public/curated data.
- Local user-entered profile, event, route, and strategy state.

## Prohibited implementation

The project must never include game memory access, injection, renderer hooks, driver/kernel inspection, traffic interception or protocol decoding, gameplay input synthesis, flea/inventory automation, enemy detection or tracking, ESP/radar, aiming/combat assistance, or an in-game overlay.

Predicted traffic is computed only from static spawns, points of interest, chokepoints, public map topology, extracts, and elapsed raid phase. Every presentation must label it as predicted educational guidance, not live player data.

## Enforcement

- Permanent rules live in root `AGENTS.md`.
- Platform interfaces expose capture and observation, never process handles for memory operations or input sending.
- Dependency/source audits search for known injection, hooking, packet-capture, automation, and memory-access packages/APIs.
- Deterministic tests assert that strategy inputs have no live-enemy concept.
- Captures are never retained by default. Any future shareable support-bundle feature must redact tokens and user path segments before it is enabled; current self-test output is local diagnostic data and may contain local paths.
- No telemetry or screenshot upload SDK is included.
- The optional TarkovTracker adapter exposes only canonical HTTPS `GET /token`
  and `GET /progress`; redirects, team access, HTTP mutation, uploads, and
  automatic apply are absent. Composition and status checks are offline, and a
  fetched snapshot cannot mutate local progress before reviewed confirmation.
