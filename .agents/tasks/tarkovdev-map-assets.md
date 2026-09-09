# Agent G — tarkov.dev map catalog, selection, and interactive presentation

Work in a fresh Orca worktree created from current `origin/main`. Read `AGENTS.md`, `docs/MAPS.md`, `docs/LICENSING.md`, and `docs/adr/0002-third-party-map-assets.md` first. Commit the finished work and report the commit hash and verification evidence.

## Product decision

The shipped product must not invent or generate its own gameplay maps. The generic training map remains a test fixture only. Runtime maps come from tarkov.dev's current map configuration and assets. Users can choose and persist the default visual variant for each EFT location; prefer the interactive variant when available.

## Ownership

- new map-catalog/cache code under `src/TarkovCompanion.Infrastructure/Maps/**`
- map selection/presentation services under `src/TarkovCompanion.Application/Services/Maps/**`
- map-specific controls/viewmodels/views under `src/TarkovCompanion.App/**`
- map-specific tests under `tests/**`
- representative, minimal map-catalog fixtures under `fixtures/maps/**`
- `docs/MAPS.md`, `docs/LICENSING.md`, `docs/THIRD_PARTY_NOTICES.md`, and ADR 0002

Avoid unrelated rewrites. Do not bundle third-party map artwork in Git or the release archive.

## Deliverables

- typed, schema-drift-tolerant parser/client for the public tarkov.dev map catalog (`the-hideout/tarkov-dev` `src/data/maps.json`) covering location, variant key, projection, `svgPath`, `tilePath`, zoom, bounds, transform, rotation, author/link, floor/layer metadata, and labels;
- bounded HTTP/cache behavior with offline fallback and provenance; no network-dependent test;
- per-location default variant settings persisted locally, with interactive-first fallback and an explicit chooser;
- map render model/control that uses tarkov.dev SVG or PNG tile assets as the background and keeps companion markers, extracts, labels, routes, risk/traffic, and filters as separate hideable/highlightable overlay layers;
- floor selection from upstream layers where available, plus pan/zoom and honest unavailable/offline/invalid-transform states;
- visible attribution/license link in the map UI and documentation of CC BY-NC-SA 4.0 plus the upstream anti-cheat restriction;
- tests proving catalog parsing, default selection persistence, interactive fallback, layer visibility, attribution, transform mapping, and offline cache behavior.

## Acceptance

- No bespoke/generated production map image is added.
- Test fixtures are clearly synthetic and never selectable as production locations.
- Third-party SVG/tiles stay runtime-cached and retain source/author/license metadata.
- All network paths are bounded and cancellation-aware.
- Solution builds with zero warnings and all tests pass.
