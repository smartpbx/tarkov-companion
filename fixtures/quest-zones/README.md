# Quest objective zones, measured

`measured-2026-09-18.json` is real data, kept small enough to read:

- `maps`: the interactive variant of Customs, Lighthouse and Interchange exactly as
  `the-hideout/tarkov-dev` publishes them in `src/data/maps.json` (bounds, transform,
  `coordinateRotation`, tile and SVG references, floor layers with their extents), retrieved
  2026-09-18. `gameMapId` is added to each entry: the game's own id for the map, from the synced
  `maps` table. tarkov.dev's file does not carry it for these three maps, and json.tarkov.dev names
  a zone's map by that id, so the quest read side needs it (see `MapGameIdResolver`).
- `objectives`: twenty task objectives as `https://json.tarkov.dev/regular/tasks` listed them on
  2026-09-17, with their `zones` (world `position`, `outline`, `bottom`/`top` elevations, name),
  item targets and counts. They were chosen to cover every shape the feed has: an outline area, a
  zone listed twice (the feed does this for 77 objectives), a `possibleLocations` objective with
  several spots and one with a single spot, a tall trigger volume, floors above the ground plan
  (Customs' dorms, Interchange's mall) and objectives with no zone at all (a kill count, a hand-in).
  `wikiUri` is null where the synced cache had no `wiki_url` column value.
- `itemNames`: names for the item ids the objectives ask for, so a test can check a name is shown
  and not an id.

Used by `tests/TarkovCompanion.UnitTests/V2MapRenderer` (`RealQuestZones`): the maps go through the
production catalog parser, the objectives through the production quest projection and scene builder,
and the expected positions are worked out from the variant alone.
