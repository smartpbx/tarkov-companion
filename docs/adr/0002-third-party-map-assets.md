# ADR 0002: Separate third-party map assets from code releases

Status: Accepted — 2026-09-09

## Context

The current `tarkov-dev` website/configuration repository reports an MIT license, while the SVG map repository uses terms that GitHub cannot reduce to a permissive SPDX identifier and that include noncommercial/share-alike obligations. Future commercial distribution is not currently authorized by assumption.

## Decision

Production map geometry, visual variants, SVG paths, tile paths, transforms, floor metadata, and attribution come from tarkov.dev's current map configuration. The application does not invent or generate substitute gameplay maps. Users can choose a default visual variant for each EFT location; the interactive variant is preferred when one exists.

Third-party artwork is never treated as project-owned and is not copied into source or release archives. A map-asset provider stores the original URI, local cache path, attribution, license identifier/text reference, retrieval date, and content hash. The app downloads/caches originals for local use only under the current terms. Companion markers, extracts, routes, traffic predictions, labels, and filters remain separate hideable/highlightable overlay layers.

The simulator and tests remain functional with a clearly synthetic code-rendered fixture that cannot be selected as a production location. Unavailable or unvalidated upstream assets degrade honestly and never weaken transform confidence.

The implementation reads `https://raw.githubusercontent.com/the-hideout/tarkov-dev/main/src/data/maps.json` through a bounded, cancellation-aware client and keeps the catalog separate from the asset cache. The UI prefers an explicitly configured PNG tile background when available, retains SVG floor-group metadata, never guesses missing asset URLs, and persists only the chosen variant key per location. Cache metadata records attribution, CC BY-NC-SA 4.0, the source URI, retrieval time, and SHA-256 content hash.

## Consequences

The MIT code release stays cleanly separable from asset obligations. Offline visual coverage depends on prior cache or distributable assets. Any future bundled/commercial map art requires a fresh written license review.
