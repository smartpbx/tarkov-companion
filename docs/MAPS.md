# Maps and last-known position

## Production catalog and artwork

Production locations and visual variants are read from the current `the-hideout/tarkov-dev` `src/data/maps.json` catalog at runtime. The client accepts unknown JSON fields, but requires location and variant identities and validates structured bounds, transforms, floors, labels, and HTTPS asset references. Interactive variants are preferred by default; a user can explicitly choose another available variant for each location, and that choice is stored in the local Tarkov Companion map settings file. The synthetic catalog in `fixtures/maps/` is test-only and is never loaded by the production catalog client.

The catalog and artwork caches are separate. Both use maximum response sizes, per-request timeouts, cancellation, atomic local writes, content hashes, and an offline fallback. The artwork cache also enforces total byte and entry-count limits by evicting its oldest entries. Cached artwork records its original URI, local path, author/link, retrieval time, SHA-256 hash, and license reference. SVG and PNG tile originals remain runtime data under the user's local application-data directory; an SVG may also have bounded local PNG render previews beside the retained original: one for the whole drawing, and one per upstream layer the maps actually ask for. Neither originals nor previews are embedded in source or release output. A variant without an explicit upstream `svgPath` or `tilePath` is unavailable; the app never guesses an asset URL.

A photographed map loads coarse level first (32 tiles or fewer, drawn under the sharp ones), is published twice a second as sharp tiles arrive, and keeps decoded tiles in a 128 MB in-memory LRU across map changes — two maximum-size sharp tile sets, with the current map's coarse underlay temporarily protected too. The artwork cache keeps running totals and rescans its directory only when they say a bound is near (8192 entries, 512 MB), giving back an eighth when one is crossed; measured 2026-09-20, a first visit to Reserve went from 53.6 s to 0.6 s for a first picture and 3.1 s complete. The app opens on the last map on screen (`#last-map` in `map-defaults.json`), and on Customs when there is none. The plan's picture is replaced at every publication, and the tablet publisher PNG-encodes it on a pool thread, so the cockpit retires a replaced picture through `PictureLeases` and frees it only when no reader holds a lease: freed under the encoder it was a native access violation on every launch (2.0.1278, fixed 2026-09-20).

The map view provides explicit location, visual-variant, and upstream floor selectors. PNG tiles or explicitly published SVG assets form the background; SVG floor selection renders only the explicitly named upstream group while retaining the cached original. Labels, extracts, spawns, locked doors, switches and quest objectives are independent hideable/highlightable overlay layers. Scroll/pointer controls provide pan and bounded zoom. When an asset, offline cache, floor, or validated transform is unavailable, the view says so and does not substitute invented geometry.

Map metadata enters the application through `IMapDefinitionCache` and `MapDataService`. The normalized cache JSON reader accepts unknown fields so a source can add data without breaking an installed client, but it rejects missing map, floor, or extract identities. Each definition keeps its `DataProvenance`; map artwork is referenced rather than embedded and must retain its own attribution and distribution terms.

Extracts pass through the reviewed repair layer described by ADR 0012 at both map read
boundaries. The primary catalog always wins by map-scoped normalized name; only a measured
omission is supplied locally, with its own provenance. The 2026-09-15 sweep found eleven such
rows across seven maps. Nine have reviewed positions and can be drawn; Icebreaker's Helicopter
and Terminal's Zubr Boat remain recognition-only because no reviewed world coordinate was
available. The evidence and exact count live in `docs/research/EXTRACT_CATALOG_COVERAGE.md`.

## Rasterising a drawing, and why it happens in a child process

A drawing is turned into a PNG by `SvgMapRasterizer`, at up to 4096 pixels along its longer side —
a budget to fill, not a ceiling to stay under, because the source is a vector and Factory's viewBox
is 130.8 by 141.2.

Three things bound that work, and each is here because of something Skia does not do:

- **One rasterisation at a time in the process.** Nothing overlaps two 64 MiB surfaces any more. A
  per-asset gate had kept one map's previews in order and did nothing about two different maps.
- **The document is measured before Skia sees it** — element count, nesting depth, and the bytes the
  surface will need. Skia does not raise a managed exception for an allocation it could not make or a
  recursion it could not finish; it faults.
- **The draw happens in a child process.** On 2026-09-19 rasterising Reserve killed the application
  with a native access violation (0xc0000005) at `sk_canvas_draw_picture`. A native fault raises no
  managed exception, unwinds nothing, and reaches no handler, so it cannot be caught — the only
  arrangement that survives one is for it to happen somewhere the application can afford to lose.
  The child is the same executable (`--rasterise-svg <in> --rasterise-preview <out>
  [--rasterise-layer <id>]`), so there is nothing extra to ship and no version skew.

A child that **cannot be started** — under `dotnet run`, a test or a tool, where the running process
is not the application's own host executable — sends the work back into this process, which is what
every build did before. A child that **started and died** does not: its failure is reported, the map
falls back to the whole drawing where the base preview exists and to its photographic tiles where
the variant has them, and nothing retries the fault in the process that must survive.

**Previews are drawn once.** One file per upstream layer, skipped whenever the file on disk is newer
than the SVG it came from, and deleted when a download replaces that SVG. Before this, every floor
rasterised onto the one `<hash>.preview.png`: a stacked Reserve load drew six full previews on every
load, and the V1 map and the V2 cockpit reading two different floors each got whichever had finished
writing last.

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
same scene, not separate sources of map truth. The Avalonia renderer draws the floor stack (below) and
disables the interior mode, since no reviewed interior renderer exists; it reports a flat-plan
fallback if another client publishes a richer canonical mode, and never draws the same flat
artwork and calls it a floor stack or interior.

The Avalonia consumer resolves reviewed artwork through an injected verified-cache resolver; it
does not fetch a manifest URL from the view. Point features use fixed-size accessible controls,
while line, area, and region geometry remains geometry. Coordinates outside reviewed bounds are
not clamped into a false edge marker. Point drawings, including hand waypoints and pings, shrink
at fit zoom while their hit targets stay accessible; three nearby marks become one count badge
until the player opens it or zooms in. Quest objectives never join a count badge or a cluster
(#797): each keeps its lettered pin, fanned around the shared spot. Dense point layers are deterministically grouped; opening a
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

**Loot focus** (the gem, and View › Loot focus; it replaced the one-way "High-value loot only" preset in #902) is emitted as a serialized sequence of the existing
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

The Layers menu and gem control offer a remembered potential-loot minimum (Any, 50k, 100k, 250k
or 500k) ranked per item or per inventory slot. That choice is evaluated by the loot source before
the stable-ID scene is reconciled, and the tablet receives both the filtered scene and the filter
that explains it; neither desktop nor tablet presents those potential spawns as live detections.

Every layer the player turns on or off in the Raid map's Layers menu (or from the paired tablet,
which drives the same renderer) is remembered by layer id in `workspace-layout.json`
(`raid.layer-visibility`), globally rather than per map, and laid over every scene build. A layer
never touched keeps its map default, such as Switches on for Labs, Reserve and Interchange (#796).
Every row is always switchable; an empty layer says "none on this map" (#902). Loot focus steps are
not saved, and a second press restores the layers it changed. Stored choices for the removed V1
layers (companion-markers, routes, risk-traffic, filters) are dropped on read.

Historical estimates carry their observation window, data-through and generation times,
coverage, calibration, transform version, model version, source, and confidence. Potential
spawns remain potential. An interior asset is renderable only after its source, licence, hash,
attribution, map/game version, and review time are present. See ADR 0015.

## Screenshot observations

Normal EFT screenshot filenames are parsed as timestamp, X/Y/Z position, quaternion, optional in-game time, and duplicate index. The quaternion is normalized and converted to a compass heading; a zero quaternion and malformed or unsafe extension are rejected. Timestamp conversion uses the supplied local UTC offset because the filename itself has no zone.

Positions are always labeled last known. By default an observation becomes stale after two minutes, and one more than 30 seconds in the future is not plotted. Both thresholds are application-side safeguards rather than claims about the game. Captured image content is neither required nor persisted for position parsing.

When the player explicitly chooses a suggested extract route on the Raid page, its lower-contact
line is stored with that raid as world positions. Debrief's map replay draws that plan as a gold
line and the raid's screenshot observations as a cyan trail. Its deviation
summary measures each screenshot to the nearest plan segment and reports average and furthest
distance. It is a comparison of sparse, player-created screenshots after the fact, never a live
location claim or an inferred path between observations.

Plan's objective route, once opened on the Raid map, is drawn in the same plan gold, dashed; a stop
that already has an objective pin shows its step number as a badge on that pin. It is recomputed
(debounced, off the interface thread) when Plan's stops, the last screenshot or the selected spawn
change. Whether it is drawn is one saved switch, the Layers row "Objective route"; "Show route" on
the Objectives card and the "Route" chip on Plan's map flip that same row, and opening a route
again never turns it back on. The maps a route was opened on are remembered across a restart. The
suggested extract route and its grey "Direct line" are two more rows under Routes.

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

The shell and Extract options card share one live countdown and name its basis. A scav join time is
never treated as the raid start, so scav time stays unknown until a screenshot clock or hand entry
supplies it.

The raid's side (read from the game's log, or set by hand in Corrections) decides whose exits the
map and Extract options show (`RaidExtractSide`, #873): a PMC raid gets PMC, shared and transit
exits, a Scav raid Scav, shared and transit ones and no spawns, matched by the faction enum and never
by display text. With the side unknown every exit is shown, the one-side ones faded on the map and
dimmed as "PMC only"/"Scav only" at the end of the list, under a "Side unknown" note.

Local pings and waypoints are stored as `RaidMark` rows (`Core`'s `MapMarkState` payload plus a
kind and timestamp) in `Config/raid-marks.json` via `IRaidMarkStore`, kept deliberately
shaped like `TarkovCompanion.CompanionProtocol`'s own `MapMark` so a paired-device sync can adapt
one to the other without a new local model. They render as `MapSceneObjectKind.Ping`/`Waypoint`
objects on their own scene layer, exactly like any other object the assembler places.

A mark is made by gesture; there is nothing to arm first. On the desktop, right-click on bare map
drops a ping and shift+right-click drops a numbered waypoint, while right-click on an existing mark
— ours or the group's — removes it. Removal beats placement: `MapSceneRendererView` raises exactly
one of `MarkerRightClicked` and `PlanRightClicked` per gesture, so the press that removes a mark can
never also place one, and the hit area is the marker's own rather than the pixel. Both arms mark the
gesture handled, so nothing above the plan can turn it into a context menu.

The tablet has no second button, so the same three outcomes ride on how a finger behaves. Drag pans
and pinch zooms, and neither ever leaves a mark. A tap on bare plan pings; a tap on an object selects
it; a press held on bare plan drops a waypoint; a press held on a mark removes it. The numbers that
tell those apart are deliberate and live in `Tablet/index.html`: a press may wander 12 CSS pixels
from where it went down (measured straight-line, not summed along the path, so a still finger's
jitter cannot add up to a drag), a tap is a press released within 350 ms, and a hold is 500 ms — which
fires while the finger is still down, so the mark appears under it. A second finger cancels the press
outright, because a pinch is never a tap. A press that placed or removed something draws a ring where
the finger was, since there is no pointer to show what was hit.

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

The camera is a centre, a zoom and a bearing over the plan rectangle. Zoom 1 is the plan rectangle
fitted to the card at bearing 0, because the projection has already done that. "Fit" (and a map
opening) is not zoom 1: it turns the map's fixed features (extracts, transits, spawn areas, locks,
hazards, place names) to the camera's bearing and scales what they cover into the card, with the
markers' inset plus 4% (`MapFitGeometry`, #551), so a tall map turned to lie along a wide card
fills it and artwork with an empty border is fitted to the map rather than to its canvas. With
too few features it fits the rectangle's corners the same way. A view still at the fit is fitted
again when the card resizes or the map is turned. Zoom limits come from the plan: out stops at the
whole rectangle on the card at the current bearing, in stops at about twice the drawn artwork's
own resolution.

Panning clamps what the viewport can see against that same rectangle, not the camera's centre
against it: the plan stops with its edge on the card's edge, and an axis whose visible span is
wider than the plan is centred rather than pinned to one side. The clamp during a drag is the
clamp the drag commits, so releasing the pointer never moves the plan somewhere else.

Follow on/off is remembered across restarts, map switches and raids; a manual pan still turns it off.

### Draw mode (#286)

The pencil on the strip switches the map between Navigate (a left-drag pans, the default) and Draw
(a left-drag draws a freehand line; a middle-drag, or a left-drag with Space held, still pans;
Escape or Done goes back). A line is simplified with Ramer–Douglas–Peucker to at most 200 points
and kept in plan units on the floor it was drawn on, so pan, zoom, turns and floor changes leave it
where it was (`RaidDrawingStore`, in memory for the session). It takes the Marks card's Just me /
Squad switch and a mark lifetime ("This raid" by default); right-click one of your lines for
Remove, Clear my drawings, scope and lifetime. Your lines are white, a squadmate's are in their
colour; Squad lines reach the group as the `drawings` field of the member's state
(`docs/GROUP_RELAY.md`).

### Inspect and Route modes (#286)

The pencil is one of four icons on the strip: Navigate, Inspect, Route, Draw (the `draw-mode` flag
still hides the pencil). In Inspect and Route a left-drag still pans, a plain click belongs to the
mode and selects nothing, and Escape goes back to Navigate. Inspect pins a popover beside the
clicked spot, read from the scene the map is drawing (`MapPointInspector`): the catalog's named
area or the nearest place label, the three nearest extracts with straight-line metres and the
suggested routes' walking pace (`TrafficRoute.MinutesFor`), modelled spawn areas within 90 m, and
objectives and loot within 60 m on the layers that are on. It places nothing. Route builds a
planned route click by click (`PlannedRoute`, at most 12 stops, Undo, Clear): each stop is a
waypoint carrying one route id and its step, the same marks a tablet's Draw route makes, so they
take the Just me / Squad switch, a lifetime ("This raid" by default), the dashed route line, and
numbers along the route. The bar shows the stop count and the total estimated minutes.

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
ground, so pressing Stack over a photograph loads the map's drawing as well, and pressing 2D puts
the photograph back if the stack is what replaced it (#923; before that the next rebuild quietly
undid the press). A map with no drawing at all, like Labs or Icebreaker, gets no Stack: the scene
withholds the mode (`MapSceneBuildRequest.FloorStackUnavailableReason`) and the disabled button's
tooltip says why. 3D interior is disabled on every map with its reason on hover: upstream lists 3D
variants but publishes no asset for them, and no interior renderer exists. A floor whose artwork will not load is left out and the status line says how many
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

### High-value loot coverage per map

Generated by `scripts/loot-coverage-table.sh` from a real publication cache; rerun it rather than
editing the rows. *Source records* is what `json.tarkov.dev` lists for the map, *Published* what
the import keeps (loose loot with candidate items; the rest are left out), *Positioned* those with
a position, and *Shown* what the layer draws with the default filter. The last column is the text
beside the loot switch in the Layers menu. There is no bundled offline snapshot: the licence of the
tarkov.dev data does not clearly allow redistributing it (#318), so a first launch without network
shows no loot until the first import.

<!-- loot-coverage:start -->
Publication `json-tarkov-dev-v1-81cdd9f9bf97f0143fe9ad06`, data through 2026-09-21, default filter.

| Map | Source records | Published | Positioned | Shown | Layers menu says |
| --- | ---: | ---: | ---: | ---: | --- |
| Customs | 857 | 306 | 306 | 232 | Partial · 17 incomplete |
| Factory | 510 | 176 | 147 | 86 | Partial · 50 incomplete |
| Ground Zero | 1,325 | 279 | 277 | 31 | Partial · 50 incomplete |
| Icebreaker | 78 | 78 | 78 | 54 | Partial · 3 incomplete |
| Interchange | 1,306 | 483 | 482 | 343 | Partial · 181 incomplete |
| Lighthouse | 1,415 | 882 | 882 | 837 | Partial · 23 incomplete |
| Reserve | 1,448 | 457 | 457 | 391 | Partial · 29 incomplete |
| Shoreline | 1,197 | 441 | 441 | 368 | Partial · 29 incomplete |
| Streets Of Tarkov | 2,224 | 942 | 942 | 672 | Partial · 40 incomplete |
| Terminal | 13 | 13 | 13 | 8 | Partial · 2 incomplete |
| The Lab | 1,009 | 690 | 687 | 603 | Partial · 19 incomplete |
| The Labyrinth | 212 | 177 | 177 | 105 | Partial · 51 incomplete |
| Woods | 697 | 269 | 269 | 236 | Partial · 23 incomplete |
<!-- loot-coverage:end -->

### Where the others started

`SpawnProximity.Near` lists the player spawn areas within 300 m of the raid's first screenshot, and
`SpawnReach.Describe` adds how long until somebody who started at one could be standing here. That
is a band and never a figure: the ends are a flat-out sprint straight at you and a careful advance
that is not straight at all, both rounded outwards onto a coarse ladder, so the band can only be
wider than the arithmetic. Scav runs get nothing at all, which is `SpawnProximity`'s existing rule.

On the Raid map (#902) the nearby spawn areas of the first five minutes are their own layer,
"Nearby spawns · first 5 min" (on), beside "All spawns" (off, kept for the whole raid); both are
modelled spawn areas, never detections. The player's past trails are the "My trail" layer (off),
read for the open map only while it is on.

"Spawn lines · first 5 min" (#914, on) draws V1's threat lines on the V2 map: one dashed red line
from each nearby PMC spawn area (a `SpawnGrouping` area, never a single point) to the player's
latest position, labelled "possible PMC spawn · 120 m". Full strength for three minutes, fading
to nothing at five (`SpawnLines.Strength`); scav and unknown-side raids get none. A radius picker
under the Map group (50 / 100 / 150 / 300 m, default 150) limits both the Nearby spawns markers
and the lines, measured from the raid's first screenshot, and is kept per map
(`raid.spawn-radius.<map id>`).
