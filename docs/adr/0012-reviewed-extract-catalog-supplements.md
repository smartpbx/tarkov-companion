# ADR 0012: Repair measured extract gaps with a reviewed supplement

Status: Accepted — 2026-09-15

## Context

The application builds both screenshot-recognition candidates and desktop extract markers from
the map data synced from `json.tarkov.dev`. A Lighthouse raid offered Hideout Under the Landing
Stage, but the endpoint published no such extract. Recognition discarded the labelled row
because it had no candidate, and the map could not list or draw a feature it had never loaded.

An all-map comparison found this was not isolated: eleven current extracts across Icebreaker,
Lighthouse, Reserve, Shoreline, The Lab, Terminal, and Woods were absent from the primary
response. Waiting for one upstream repair would leave the app confidently incomplete, while
replacing the primary source would duplicate synchronization, cache, validation, and provenance
behavior for every map.

## Decision

`json.tarkov.dev` remains the primary runtime structured-data source. Infrastructure contains a
bounded reviewed supplement comprising only the eleven omissions measured in
`docs/research/EXTRACT_CATALOG_COVERAGE.md`. The normalized seventeen-record primary input,
thirteen pinned current lists, canonical-catalog hash, and explicit Factory, Ground Zero, and Lab
variant mappings are retained in
`fixtures/extract-catalog/coverage-2026-09-15.normalized.json`; a deterministic test must subtract
that fixture back to the exact eleven rows. Each row has a companion-owned ID, canonical map and
display name, explicitly supported faction or `unknown`, optional world coordinate, confidence,
review time, source-update time, and exact source references.

The supplement is merged into `IMapDefinitionCache` for screenshot recognition. Rows with a
reviewed position are also merged into `IMapFeatureCatalog` for desktop markers; a row without
one remains listable with “location unavailable,” retains its reviewed faction in the desktop
list, and is never plotted. Identity is map-scoped and ignores case and punctuation. A primary
row always wins, so an upstream addition retires the local row without a release or duplicate
marker. No database migration or second runtime request is added.

Nine coordinate facts come from SPT-DynamicMaps static map configurations pinned to commit
`4944764f5f6c42d152dca6bd1b5371c4f6212a9e`; pinned EFT Wiki revisions corroborate their names
and explicitly published sides. Icebreaker's Helicopter and Terminal's Zubr Boat have no
reviewed world coordinate and use only their permanent list references. The Lab list does not
publish a side for Medical Block Elevator, so that row remains `unknown`.

The reviewed use transcribes factual static records only. SPT-DynamicMaps' `Plugin/LICENSE` is
MIT, and its map/data credit file is retained beside that license. Its separately licensed SVG
layers, artwork, icons, marker assets, source code, wording, layout, and live behavior are not
copied. The review is recorded in `docs/LICENSING.md` and `docs/THIRD_PARTY_NOTICES.md`.

Recognition also gains a source-independent fallback. A line carrying the game's structural
`EXFIL` slot label that cannot clear catalog matching becomes a conservative, provenance-bearing
observation rather than generic discarded text. It can appear in the extract list immediately,
but has no map marker and says its location is unavailable until a trusted coordinate exists.
The fallback is bounded to sixteen unique rows and 64 characters per name, applies the ordinary
candidate-confidence floor when confidence exists, and requires a minimally name-like value.
Headers, status-only rows, measurements, and one-character readings remain unmatched; competing
near-equal catalog candidates remain ambiguous. Trusted catalog matches take priority in the
relay-sized active list, and an id beginning `catalog-gap:` is prohibited from claiming a real
map marker through loose label matching.

## Consequences

The reported Lighthouse extract and the other eight coordinate-bearing omissions are available
to both recognition and the map. The two positionless omissions are available to recognition
without an invented marker. Future upstream omissions remain visible from a user's
extract-screen screenshot instead of silently disappearing. The static repair set is easy to
audit and has a defined retirement behavior, but it must be swept after map/extract changes
rather than treated as timeless game data.

Every added or changed supplement row requires the same measured comparison, permanent factual
source, license review, and tests for primary precedence. A coordinate or faction may be recorded
only when its evidence supports it; otherwise that field remains absent or unknown. Runtime code
must not scrape either review source.
