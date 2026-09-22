# High-value loot-spawn sources

## Authority split

`json.tarkov.dev` remains the primary runtime source. Its maps payload publishes loose-loot world
positions with candidate item IDs and container positions with container-type IDs. Its item
catalog supplies canonical identity, display name, category, footprint, flea value, and trader
value. Loose-loot candidate pools can therefore be normalized from one structured public source;
item metadata is never used to infer a location.

The maps payload does not publish explicit floor IDs, spawn probability, respawn behavior, or the
candidate contents of each container type. Those facts remain unknown unless a separately reviewed
and licensed curated input supplies them. A curated bundle supplements a measured gap; it does not
replace a primary-source fact merely because its location is more convenient.

Reviewed curated inputs use a versioned manifest plus one content document. The production
`TarkovDevLootSpawnNormalizer` instead composes the exact bounded `/{mode}/maps` and
`/{mode}/items` response documents with the separately hashed and reviewed tarkov.dev map catalog.
It does not trust a caller-supplied object graph in place of those response bytes. The composite
content identity frames each exact base response and language-normalized document, game mode,
language, and the catalog URI and SHA-256.
The published identity also retains each maps, items, language-view, and catalog SHA-256
separately, so a restart does not reduce exact source provenance to an opaque composite hash.
`TarkovDevLootSpawnRefreshService` obtains those three inputs serially, publishes only a complete
validated candidate, and quarantines a bounded reason while retaining the configured last-known-good
head on a refused or unavailable refresh.

The production mapping was measured against the regular-mode feed on 2026-09-16: 17 source map
records contained 5,196 loose-loot positions, 37,774 candidate memberships, and 7,098 container
positions. Reviewed aliases map 16 of those records onto 13 runtime-supported canonical maps;
`ground-zero-tutorial` remains explicitly unmatched rather than being guessed into Ground Zero.
The largest source-map pool aggregate was 7,474 candidate memberships, which is why a snapshot has
a 16,384-candidate ceiling while the complete bundle remains capped at 65,536. These measurements
describe accepted input capacity, not spawn probability or expected value. The exact maps response
used for that recount has SHA-256
`9ce7b2b7d2677ebbee6a33c3208bcd712300554e2f83e0c29be031282701ecce`.

`fixtures/loot-spawns/source-v1` and the normalizer's synthetic map tests remain test-only; their
coordinates and item IDs make no claim about Escape from Tarkov. Desktop startup now restores the
restart-durable head, and its shared background data refresh publishes the production adapter's
validated result without forcing a second maps/items download. The V2 Raid host that inserts the
typed result into its canonical scene remains owned by issue #286.

## Production normalization

The production adapter preserves these source boundaries:

- Each response carries its exact `regular`, `pve`, or `pvp-season` source key. Cross-mode or
  unlabelled joins are refused.
- A loose-loot `items` array is candidate membership only. One member becomes a single-known-item
  pool; more than one remains explicitly unweighted. No probability or expected value is derived
  from membership or source ordering.
- Flea gross value, trader value, footprint, name, and category retain field-level item-endpoint
  evidence. Flea net remains unknown because the feed does not publish the applicable fee result.
- Container positions contribute to known-record coverage, but no marker is published for one
  until a reviewed source supplies that container type's candidate contents.
- A complete finite world point is plotted only when it is inside the selected catalog variant's
  world bounds, the reviewed transform projects it, and the result is inside the derived scene
  bounds. Otherwise the pool remains map-only; no coordinate is invented.
- Floors are attached only through explicit catalog extents; the base floor also requires an
  explicit height bound rather than the parser's unbounded fallback. An unresolved floor remains
  empty. It can render on a one-floor map, but a multi-floor view suppresses it until the layer is
  known.
- Every supported map receives measured known, published, positioned, floor-resolved, and
  unresolved counts. Unsupported source maps and skipped catalog locations produce diagnostics;
  a zero or grossly mismatched first publication is refused instead of becoming a hollow head.
- Duplicate JSON members (including case variants), invalid identities, oversized pools,
  aggregate-budget violations, ambiguous aliases, unknown item IDs, and chronology or provenance
  violations cannot become the publication head.

The map layer's default filter asks for no confidence score (the feed is unscored), accepts prices
up to seven days old, and ranks by the flea price before fee when the fee is unknown or by one market
alone as a lower bound; a snapshot from an older catalog revision of the same map is still drawn,
labelled "positions may be off" (#563).

## Version 1 bundle

The manifest records:

- schema and dataset version;
- SHA-256 identity of the exact UTF-8 content bytes;
- generated and data-through UTC timestamps;
- source class, stable identifier, reference, licence, confidence meaning, and producer;
- per-map known, published, positioned, floor-resolved, and unresolved counts.

The import context separately supplies the bounded source identities, references, licence terms,
and confidence meanings that have completed review. A manifest cannot make itself trusted by
labelling itself curated. Public structured bundles and item fields must name the exact canonical
`json.tarkov.dev/{mode}/maps` and `json.tarkov.dev/{mode}/items` HTTPS endpoints respectively, and
a public maps bundle cannot resolve candidate values through a different game mode's item catalog.

The content records one snapshot per map and transform version. Each spawn has a stable ID,
label, explicit location precision, explicit floor IDs, bounded geometry when known, and either
one known item or an explicitly unweighted candidate pool. Probability and respawn behavior stay
unknown because version 1 does not accept unsupported claims for either field. Candidate item
metadata must resolve through a caller-supplied `json.tarkov.dev` catalog entry carrying public
structured-data provenance per value field, so unavailable, stale, ambiguous, or corrected prices
are not flattened into a manufactured current value. A bundle marked `publicStructuredData` must
itself identify `json.tarkov.dev`; curated bundles retain their distinct source class and reference.

Unknown JSON fields are accepted for forward compatibility. Required fields, schema compatibility,
exact-byte lowercase SHA-256 content identity, canonical strings, duplicate JSON members,
timestamps, collections, nesting, aggregate domain limits, unique IDs,
map/transform identity, floor IDs, geometry bounds, pool shape, item IDs, and measured coverage are
validated before publication. A stale, future-dated, malformed, hash-mismatched, incompatible, or
superseded attempt is quarantined and cannot replace the atomic last-known-good head. Cancellation
does not publish or quarantine a partial attempt. The imported UTC time is retained separately from
the manifest's generated and data-through times. One publication store is scoped to one reviewed
source authority; a curated source cannot replace a primary-source head merely by carrying a later
timestamp. A later bundle also cannot silently move data-through time backwards, drop a published
map, or reduce its known, published, positioned, or floor-resolved counts.
For candidate metadata that is joined from the separately evidenced item catalog, a later import
cannot replace a matching spawn/item field with older evidence, and one import observation cannot
claim two different current values for the same source generation.

Both process-lifetime and restart-durable implementations sit behind
`ILootSpawnSourcePublicationStore`. `DurableLootSpawnPublicationStore` writes a single bounded,
versioned frame containing the exact serialized bundle length and SHA-256, uses a same-directory
write-through temporary file, and atomically retains the prior generation as `.previous`. Every
publisher takes an inter-process lease and re-reads that on-disk head before applying the same
source, chronology, coverage, item-evidence, and identity monotonicity policy. This prevents a
second instance with stale memory from replacing a newer publication. A corrupt or oversized head
is never deserialized as data: the store validates the frame, payload hash, JSON, and domain
invariants, retains corrupt primary/fallback files in two fixed diagnostic paths, and recovers the
validated previous generation when available. The payload is capped at 256 MiB, quarantine remains
bounded at 64 entries, and scratch names are removed after success or failure.

A legitimate wipe or reviewed upstream removal uses the separate
`IReviewedLootSpawnPublicationReplacementStore`; automatic refresh cannot invoke it. Authorization
names the exact current content SHA-256 plus reviewer, reason, and UTC time. It may bypass only the
coverage and item-removal ratchets: source authority, chronology, and same-generation conflict
checks remain mandatory. The durable implementation writes a bounded atomic authorization journal
before attempting the replacement, so a stale reviewer cannot overwrite a concurrently advanced
head and a failed disk write does not erase which exact candidate was approved. See ADR 0019.
