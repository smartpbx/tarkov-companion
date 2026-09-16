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

Every normalized input is shipped as a versioned manifest plus one content document. No production
normalized bundle or maps-to-bundle runtime adapter is currently checked in.
`fixtures/loot-spawns/source-v1` is synthetic and test-only; its coordinates and item IDs make no
claim about Escape from Tarkov. Coverage through this new publication path is therefore zero
supported maps until the primary maps payload is reproducibly normalized (and any curated gap is
reviewed). The existing raw maps feed is not itself evidence that this importer has published a
last-known-good snapshot.

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

The current publication store is process-lifetime only and deliberately sits behind
`ILootSpawnSourcePublicationStore`. Durable offline storage and application composition remain a
separate integration slice; until then, the importer does not claim restart-persistent offline
availability.
