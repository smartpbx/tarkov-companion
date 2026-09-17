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

## V2 shared scene

V2 map renderers consume one platform-independent scene snapshot rather than translating the
desktop canvas. The snapshot carries the selected map and floor, camera, layer state, stable
object IDs, point/line/area/region geometry, transform version, typed fact semantics, and reviewed
asset manifests. Its list alternative and hit testing use the same visibility state as the visual
map. Flat 2D is the baseline; floor-stack and interior presentations are capabilities over the
same scene, not separate sources of map truth. The current Avalonia renderer deliberately
disables both richer presentation buttons: the scene asset contract does not yet associate a
`Floor2D` asset with a floor ID, and no reviewed interior renderer exists. It keeps the real
floor filter available and reports a flat-plan fallback if another client publishes a richer
canonical mode; it never draws the same flat artwork and calls it a floor stack or interior.

The Avalonia consumer resolves reviewed artwork through an injected verified-cache resolver; it
does not fetch a manifest URL from the view. Point features use fixed-size accessible controls,
while line, area, and region geometry remains geometry. Coordinates outside reviewed bounds are
not clamped into a false edge marker. Dense point layers are deterministically grouped; opening a
cluster exposes every source record through the searchable, paged list. That list includes every
visible object even when a layer opts out of the older scene list summary. Bare-map hit testing
considers only the individual markers and geometry actually drawn, so selecting a cluster cannot
silently choose a hidden member. Pan, zoom, fit, floor, mode, and layer actions emit
revision-checked scene changes and wait for the canonical snapshot to return before another
change is sent. Selection, clear, and camera acknowledgements retain existing control
collections and do not resolve unchanged artwork again.

The host supplies the renderer's selected string resources, number/date culture, and time zone;
evidence timestamps never use process culture or the development machine's local zone. Semantic
glyphs, text, and automation names keep historical estimates, local/team last-known records,
PMC/Scav/shared extracts, and offered/not-offered/unknown states distinct. Refusals use an
assertive text live region, while dense-scene and selection changes use polite text peers. Narrow
windows reflow details below the plan and remain scrollable at a 320-DIP viewport and enlarged
interface text. The packaged `--map-renderer-gallery` and
`--map-renderer-gallery --map-renderer-large-text` launches provide deterministic Windows
screenshots, UI Automation interactions, cluster/search/page coverage, and measured touch-target
evidence for those layouts.

The desktop renderer may receive `HighValueLootLayerResult` beside the scene. It requires the
result's layer, map, transform, applied service filter, and every positioned result object to match
the canonical publication exactly, then joins typed entries to objects by stable object ID.
Map-only and unresolved-floor entries stay in the same paged accessible list with explicit
`List only`, location, and floor states. They are not assigned a point. The existing deterministic
point clustering applies to positioned loot objects; the typed list remains the route to every
source record.

The **High-value loot only** preset is emitted as a serialized sequence of the existing
revision-checked layer-visibility changes. It retains built-in orientation layers, visible hazards,
the selected object's layer, and any visible safety/context layers in the host's bounded preserve
set; it leaves camera, floor, and selection unchanged. The sequence is deliberately non-atomic:
if a later change conflicts, earlier confirmed changes remain applied, the remaining changes stop,
and the renderer asks the user to review the current layers before retrying. Filter requests carry
a unique change ID plus the expected scene revision, map, and transform. They are rebuilt by the
host through the loot-layer service and returned as one matching scene/result
publication; the renderer applies only the minimum-tier display projection locally. Host-supplied
category and floor choices have bounded reads and a 12-choice rendered cap; an accessible message
says when additional choices were omitted. The current scene change contract has no
create-waypoint operation, so selected loot cannot yet be handed off as a planning stop without a
new shared command owned by the map contract.

Historical estimates carry their observation window, data-through and generation times,
coverage, calibration, transform version, model version, source, and confidence. Potential
spawns remain potential. An interior asset is renderable only after its source, licence, hash,
attribution, map/game version, and review time are present. See ADR 0015.

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

## Raid cockpit (V2)

`RaidCockpitViewModel` (`src/TarkovCompanion.App/ViewModels/V2/Raid/`) is the first production
caller of `MapSceneAssembler`: it turns the V1 map's render model, the extracts and quest-objective
overlays it already carries, the high-value-loot layer (`IHighValueLootRuntimeSource`), and local
marks into one canonical `MapSceneSnapshot`, and hosts the existing `MapSceneRendererView`
unchanged. It follows the raid's current map, offers a manual map picker over `MapViewModel`'s own
catalog, and reuses `RaidPageViewModel` for the timer and extract panel rather than recomputing
either.

Local pings and waypoints are stored as `RaidMark` rows (`Core`'s `MapMarkState` payload plus a
kind and timestamp) in `Config/raid-marks.json` via `IRaidMarkStore`, kept deliberately
shaped like `TarkovCompanion.CompanionProtocol`'s own `MapMark` so a paired-device sync can adapt
one to the other without a new local model. They render as `MapSceneObjectKind.Ping`/`Waypoint`
objects on their own scene layer, exactly like any other object the assembler places.

The historical-traffic layer is registered (`HistoricalTrafficRuntimeService`) but not yet
evaluated: nothing in this pass supplies the installed `TrafficModelPublication` its scope
(game version, wipe, cohort) needs, so the cockpit shows a static "no installed model" notice
rather than inventing one. The V2 renderer also does not yet decode the reviewed map asset into a
picture — only its hashed identity and licence are carried into the scene — so the cockpit shows
every layer's objects on an empty plan until that rasterization work lands.
