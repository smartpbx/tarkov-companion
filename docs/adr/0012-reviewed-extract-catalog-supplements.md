# ADR 0012: Repair measured extract gaps with a reviewed supplement

Status: Accepted — 2026-09-15

## Context

The application builds both screenshot-recognition candidates and desktop extract markers from
the map data synced from `json.tarkov.dev`. A Lighthouse raid offered Hideout Under the Landing
Stage, but the endpoint published no such extract. Recognition discarded the labelled row
because it had no candidate, and the map could not list or draw a feature it had never loaded.

An all-map comparison found this was not isolated: nine current extracts across Lighthouse,
Reserve, Shoreline, The Lab, and Woods were absent from the primary response. Waiting for one
upstream repair would leave the app confidently incomplete, while replacing the primary source
would duplicate synchronization, cache, validation, and provenance behavior for every map.

## Decision

`json.tarkov.dev` remains the primary runtime structured-data source. Infrastructure contains a
bounded reviewed supplement comprising only the nine omissions measured in
`docs/research/EXTRACT_CATALOG_COVERAGE.md`. Each row has a companion-owned ID, canonical map and
display name, faction, world coordinate, confidence, review time, source-update time, and exact
source references.

The supplement is merged at both existing read boundaries: `IMapDefinitionCache` for screenshot
recognition and `IMapFeatureCatalog` for desktop markers. Identity is map-scoped and ignores
case and punctuation. A primary row always wins, so an upstream addition retires the local row
without a release or duplicate marker. No database migration or second runtime request is added.

The coordinate facts come from SPT Leaderboard map files pinned to commit
`389e23571d7d6fe8c3da354f80fdca9cd14e9098`; the current EFT Wiki extract lists independently
corroborate the names and sides. The reviewed use transcribes factual records only. No source
code or artwork is copied. The source is MIT licensed, its license text and attribution are
retained, and the review is recorded in `docs/LICENSING.md` and
`docs/THIRD_PARTY_NOTICES.md`.

Recognition also gains a source-independent fallback. A line carrying the game's structural
`EXFIL` slot label that cannot clear catalog matching becomes a conservative, provenance-bearing
observation rather than generic discarded text. It can appear in the extract list immediately,
but has no map marker and says its location is unavailable until a trusted coordinate exists.
Unlabelled OCR text remains unmatched; competing near-equal catalog candidates remain ambiguous.

## Consequences

The reported Lighthouse extract and the other eight measured omissions are available to both
recognition and the map. Future upstream omissions remain visible from a user's extract-screen
screenshot instead of silently disappearing. The static repair set is easy to audit and has a
defined retirement behavior, but it must be swept after map/extract changes rather than treated
as timeless game data.

Every added or changed supplement row requires the same measured comparison, pinned coordinate
source, independent current-list corroboration, license review, and tests for primary precedence.
Runtime code must not scrape either review source. If a trusted coordinate cannot be established,
the screenshot-only row remains listable and unplotted.
