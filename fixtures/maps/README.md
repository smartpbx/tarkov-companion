# Map fixtures

- `tarkov-dev-catalog.synthetic.json` — a made-up catalog in the upstream shape, for parser tests.
  Never loaded by the production catalog client.
- `training-ground.json` — a code-authored test map. Not distributable third-party artwork.
- `catalog-geometry-2026-09-18.json` — every interactive variant `the-hideout/tarkov-dev` publishes
  in `src/data/maps.json`, retrieved 2026-09-18, reduced to the fields that decide where the map is
  drawn: `transform`, `coordinateRotation`, `bounds`, `svgBounds`, the tile and SVG references,
  tile size, zoom range and the floor layers with their extents. Place names, the labels array and
  everything else authored were dropped; this is the geometry, not the artwork.

  `svgViewBox` records each published drawing's own `viewBox` width and height, measured from the
  file at `svgPath` on the same day. That is the one number the catalog does not carry and the one
  the aspect-ratio tests need: what shape the picture actually is, independent of any rectangle
  this application works out for it.

  Used by `tests/TarkovCompanion.UnitTests/V2MapRenderer/MapPlanAspectTests.cs`, through the
  production catalog parser, to check that every map is drawn at its own shape.
