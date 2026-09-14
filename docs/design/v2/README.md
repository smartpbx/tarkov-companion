# Tarkov Companion v2 UI concepts

These directional renders support GitHub issues #256 and #318. They make the proposed information
architecture and interaction priorities concrete; they are not pixel-perfect implementation
contracts and are not runtime assets.

The images were generated on 2026-09-14 with the built-in image-generation workflow, using one
prompt per concept and the current Raid screenshot as the initial product reference. All prompts
shared these invariants:

- a practical, high-fidelity standalone Windows/tablet application rather than concept art;
- a graphite, slate, off-white, cyan/teal, and restrained amber design system;
- five primary workspaces: Raid, Intel, Plan, Team, and Debrief;
- readable typography, clear hierarchy, explicit focus/selection, and touch-friendly tablet UI;
- no process-memory access, generated game input, or in-game overlay;
- no live-enemy claim: traffic is historical/modelled and team waypoints are manual;
- the tablet is a securely paired extension of the desktop companion, not a phantom group member;
- screenshot capture is contextual, user-triggered, and central to item and raid decisions;
- visible provenance, freshness, coverage, confidence, and degraded states.

## Prompt set and files

- `v2-home-setup-concept.png` — first-run readiness, sample data, current plan, system health,
  recent intelligence, and explicit privacy defaults.
- `v2-raid-intelligence-concept.png` — the flagship Raid cockpit with a phase-aware historical
  traffic heatmap, route alternatives, provenance, confidence, and extract planning.
- `v2-intel-workspace-concept.png` — unified item/ammo/key/craft search with price history,
  source comparison, data coverage, and explainable keep/sell/use guidance.
- `v2-plan-workspace-concept.png` — next-raid quest bundling, route planning, requirements,
  loadout readiness, and historical contact trade-offs.
- `v2-team-tablet-concept.png` — responsive Team/tablet view with roles, freshness, manual shared
  waypoints, objective readiness, expiring device invite, and revocable session identity.
- `v2-tablet-desktop-control-concept.png` — revised paired-tablet direction with Follow, Control,
  and Independent modes; desktop workspace/map controls; manual marks; and capture arming.
- `v2-stash-scan-concept.png` — a guided multi-screenshot session that reconstructs and deduplicates
  a stash, recognises ammo/key storage, and produces a manual keep/sell/use/organise plan.
- `v2-loot-scan-concept.png` — a fast in-raid screenshot result that ranks visible loot by value per
  square plus current/future quest, hideout, craft/barter, scarcity, and carried-inventory context.
- `v2-high-value-loot-layer-concept.png` — the Raid workspace filtered to potential high-value loot
  locations, with value/quest/floor controls, compact provenance, routing, and paired-tablet parity.

## Interpretation notes

- All prices, counts, confidence values, times, member names, and routes are illustrative.
- The pictured QR code is decorative and must not be treated as a working invite or credential.
- A "one go" stash scan is one guided session and may require several overlapping screenshots.
- Sort and swap output is advice for manual action; the app never moves or loots an in-game item.
- Map imagery and item thumbnails are placeholders for a reviewed production asset pipeline.
- Generated text and icon details must be rebuilt as native controls, not cropped from these images.
- Accessibility, responsive behaviour, keyboard flow, loading/error states, and data correctness
  still require native prototypes and the acceptance evidence defined in issue #256.
