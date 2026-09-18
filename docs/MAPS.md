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

## The plan rectangle: one projection for the artwork and everything on it

`MapPlanProjection.For(renderModel)` (Application) answers where the artwork on screen is, as a
rectangle in the same Leaflet map units `MapCatalogTransform.TryProject` puts a world position
into. It is the one place that knows a map drawn from PNG tiles covers the tile grid's own
outward-snapped rectangle while a map drawn from the reviewed SVG covers `svgBounds ?? bounds`.

Both renderers project through it. V1's `MapCanvasCoordinateMapper.Create` maps that rectangle
onto its canvas; the V2 Raid cockpit hands it to the scene as `MapSceneSnapshot.Bounds`, so every
coordinate in the scene — extracts, place names, quest objectives, spawn areas, the player's
position, their trail, squadmates, loot spawns — is already in the rectangle's units and lands
where the artwork puts it. The renderer's own `MapSceneProjection` then fits that rectangle into
the card (contain, centred) and scales objects by the same two axis scales, so the artwork and the
markers cannot drift apart.

Before this the cockpit declared a fixed 0-100 box as the plan's bounds and filled it with Leaflet
coordinates. On any real map the two described different rectangles, which put Customs' extracts
against the plan's edges, counted the mismatch as "off-plan" objects, and let a follow or a pan
put the camera on a point outside the plan that every later gesture was then clamped away from.

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
rather than inventing one.

The cockpit hands the renderer one decoded background image per scene, whichever artwork V1 is
showing. For a drawn map that is the rasterized reviewed SVG for the selected floor. For a map
drawn from PNG tiles it is V1's own loaded tile grid — the same plan, the same reviewed assets,
the same level choice — composed onto one surface the size of the grid, scaled down when the grid
is larger than 4096 pixels on its long edge. A tile that never arrived is left undrawn, so it is
blank in its own square and every other tile is still in the right place, which is what V1's
"160 of 170 tiles · the rest are blank" status line has always meant.

### Camera

The camera is a centre, a zoom and a bearing over the plan rectangle. Zoom 1 is the fit, because
the projection has already fitted the rectangle to the card, so a map opens (and "Fit" returns)
with the whole plan on screen at whatever size the card is, and a wider card shows the same plan
larger rather than at a different zoom. Zoom limits come from the plan: out stops at the fit, in
stops at about twice the drawn artwork's own resolution.

Panning clamps what the viewport can see against that same rectangle, not the camera's centre
against it: the plan stops with its edge on the card's edge, and an axis whose visible span is
wider than the plan is centred rather than pinned to one side. The clamp during a drag is the
clamp the drag commits, so releasing the pointer never moves the plan somewhere else.

### Stacked floors

`MapSceneMode.FloorStack2D` is drawn, not reported unavailable. A scene that wants it declares one
`MapSceneAssetKind.Floor2D` asset per floor, each naming its floor through `MapSceneAsset.FloorId`,
and the renderer draws them as plates around the floor being read: that floor solid and outlined at
offset zero, the rest quieted above and below it, each carrying its own floor name. The plates are
ordered by the catalog's own height bands (`FloorStack.Elevation`, through the renderer's
`floorElevationResolver` seam) rather than by the order upstream listed them, and the floor picker
is a ladder in the same order, top floor first, with a step-up/step-down pair beside it.

Every plate draws into exactly the projection's plan rectangle — `MapLeft`/`MapTop`/`MapWidth`/
`MapHeight`, identical on all of them — and only the vertical offset differs. That keeps the
projection contract above: the rectangle the artwork draws into is the rectangle objects project
into, on every floor, so a marker sits on its own floor's picture. Nothing is sheared or scaled; a
plate squashed to look three-dimensional would no longer be that rectangle. A stacked map reserves
canvas headroom above and below the plan (`MapSceneProjection`'s `headroomAbove`/`headroomBelow`)
so the plates either side are not clipped at the card's edge, capped at 45% of the card.

The plates have to be the drawing. A tile grid and a drawing cover different rectangles of the same
ground, so the cockpit refuses to stack a tile-drawn map and says which press would fix it
("Stacked floors need the drawing — choose it above"); a map with no drawing at all, like Labs,
says so instead. A floor whose artwork will not load is left out and the status line says how many
of how many arrived — a gap in the stack is honest, a blank plate at the right height is not.

Automatic floor selection (`MapViewModel.FloorSource`) now states what it did: the floor it took
from your last screenshot, that there is no screenshot yet, or that your height matches no floor
here. Choosing a floor by hand clears it.

### Artwork chooser and layer counts

The Raid cockpit offers every piece of artwork this location publishes that can actually draw. A row
is one picture rather than one catalog variant: the interactive variant carries both a drawing and a
tile photograph on most maps, and upstream publishes no asset path at all for the 2D and 3D variants
it lists, so those never reach the chooser. Both kinds of choice go through `MapViewModel`
(`SelectVariantAsync`, `ToggleArtworkAsync`) and are remembered per map by its selection service.
ADR 0015's asset attribution for whichever artwork is showing sits under the chooser, and shows on
a map that has no choice too.

Every layer switch carries the count of what it would draw ("Extracts 30", "Labels 24"), taken from
the scene's own objects — and from the high-value-loot layer's currently visible set for that one.
A layer holding nothing reads "Quest objectives · none" and is disabled, rather than switching the
map to an empty plan.

### Where the others started

`SpawnProximity.Near` lists the player spawn areas within 300 m of the raid's first screenshot, and
`SpawnReach.Describe` adds how long until somebody who started at one could be standing here. That
is a band and never a figure: the ends are a flat-out sprint straight at you and a careful advance
that is not straight at all, both rounded outwards onto a coarse ladder, so the band can only be
wider than the arithmetic. Scav runs get nothing at all, which is `SpawnProximity`'s existing rule.
