# ADR 0002: Separate third-party map assets from code releases

Status: Accepted — 2026-09-09

## Context

The current `tarkov-dev` website/configuration repository reports an MIT license, while the SVG map repository uses terms that GitHub cannot reduce to a permissive SPDX identifier and that include noncommercial/share-alike obligations. Future commercial distribution is not currently authorized by assumption.

## Decision

Map geometry/configuration may be normalized with source metadata when its license permits. Third-party artwork is never treated as project-owned and is not silently copied into source. A map-asset provider stores the original URI, local cache path, attribution, license identifier/text reference, retrieval date, and content hash. The app may download/cache an original for local use only when current terms allow it.

The base app and simulator remain functional with code-rendered fixture maps. Unavailable or unvalidated assets degrade honestly and never weaken transform confidence.

## Consequences

The MIT code release stays cleanly separable from asset obligations. Offline visual coverage depends on prior cache or distributable assets. Any future bundled/commercial map art requires a fresh written license review.
