# Maps and last-known position

## Production catalog and artwork

Production locations and visual variants are read from the current `the-hideout/tarkov-dev` `src/data/maps.json` catalog at runtime. The client accepts unknown JSON fields, but requires location and variant identities and validates structured bounds, transforms, floors, labels, and HTTPS asset references. Interactive variants are preferred by default; a user can explicitly choose another available variant for each location, and that choice is stored in the local Tarkov Companion map settings file. The synthetic catalog in `fixtures/maps/` is test-only and is never loaded by the production catalog client.

The catalog and artwork caches are separate. Both use maximum response sizes, per-request timeouts, cancellation, atomic local writes, content hashes, and an offline fallback. The artwork cache also enforces total byte and entry-count limits by evicting its oldest entries. Cached artwork records its original URI, local path, author/link, retrieval time, SHA-256 hash, and license reference. SVG and PNG tile originals remain runtime data under the user's local application-data directory; an SVG may also have a bounded local PNG render preview beside the retained original. Neither originals nor previews are embedded in source or release output. A variant without an explicit upstream `svgPath` or `tilePath` is unavailable; the app never guesses an asset URL.

The map view provides explicit location, visual-variant, and upstream floor selectors. PNG tiles or explicitly published SVG assets form the background; SVG floor selection renders only the explicitly named upstream group while retaining the cached original. Labels, companion markers, extracts, routes, risk/traffic predictions, and filters are independent hideable/highlightable overlay layers. Scroll/pointer controls provide pan and bounded zoom. When an asset, offline cache, floor, or validated transform is unavailable, the view says so and does not substitute invented geometry.

Map metadata enters the application through `IMapDefinitionCache` and `MapDataService`. The normalized cache JSON reader accepts unknown fields so a source can add data without breaking an installed client, but it rejects missing map, floor, or extract identities. Each definition keeps its `DataProvenance`; map artwork is referenced rather than embedded and must retain its own attribution and distribution terms.

Extracts pass through the reviewed repair layer described by ADR 0012 at both map read
boundaries. The primary catalog always wins by map-scoped normalized name; only a measured
omission is supplied locally, with its own provenance. The 2026-09-15 sweep found eleven such
rows across seven maps. Nine have reviewed positions and can be drawn; Icebreaker's Helicopter
and Terminal's Zubr Boat remain recognition-only because no reviewed world coordinate was
available. The evidence and exact count live in `docs/research/EXTRACT_CATALOG_COVERAGE.md`.

## Coordinate transforms

A transform is usable only when all values are finite, world and visual extents are positive, and floor ranges are finite, non-empty, and non-overlapping. World X/Z is mapped to the visual plane, with configured flips and rotation. World Y selects a floor using an inclusive lower and exclusive upper bound.

For tarkov.dev interactive variants, the four published transform values are applied with tarkov.dev's Leaflet semantics: rotate world X/Z about the origin by `coordinateRotation`, then apply X/Y scale and offset, with the Leaflet Y-axis inversion. Tile planning uses the published bounds, tile size, and minimum/maximum zoom and refuses plans over the safe tile-count limit.

If a map has no transform, or validation fails, the application returns a clear status and no marker. It never estimates, clamps, or borrows coordinates from another map. `fixtures/maps/training-ground.json` is a code-authored test map and is not distributable third-party artwork.

## Screenshot observations

Normal EFT screenshot filenames are parsed as timestamp, X/Y/Z position, quaternion, optional in-game time, and duplicate index. The quaternion is normalized and converted to a compass heading; a zero quaternion and malformed or unsafe extension are rejected. Timestamp conversion uses the supplied local UTC offset because the filename itself has no zone.

Positions are always labeled last known. By default an observation becomes stale after two minutes, and one more than 30 seconds in the future is not plotted. Both thresholds are application-side safeguards rather than claims about the game. Captured image content is neither required nor persisted for position parsing.

## Extracts

Recognized active extracts are joined to cached static extract metadata by canonical ID. An active extract remains visible when its static position is missing, with explicit guidance that no marker can be shown. OCR confidence and source remain attached to the active observation.

A structurally labelled `EXFIL` screenshot row that cannot be matched to either the primary or
reviewed catalog is retained as an offered extract with conservative confidence. It appears in
the compact extract list as `Location unavailable` and is not plotted. Speculative rows are
candidate-confidence, name-shape, 64-character, and sixteen-row bounded; trusted matches take
priority. A `catalog-gap:` row cannot highlight a static marker by a partial name. Unlabelled OCR
text and near-tied catalog matches remain unmatched or ambiguous rather than becoming map facts.
