# Event definitions

Escape from Tarkov runs seasonal events in which some consumables have effects that differ per
player. The companion lets a player mark each applicable item as Safe or Allergic. The files in
the app's events directory say which events exist and which items they apply to.

json.tarkov.dev has no events endpoint, so nothing here is downloaded. Every file is written by a
person, and the app treats it that way: the provenance it records is "local event definition"
with confidence capped at 0.60, whatever the file itself claims.

## Where the app reads from

The app loads every `*.json` file directly inside its events directory (sub-folders are ignored):

- normal install: `%LOCALAPPDATA%\TarkovCompanion\Config\Events`
- portable install (`portable.flag` next to the executable): `Data\Config\Events`

Files in this repository directory are not loaded by themselves; copy the ones you want into the
folder above. A file edited by hand while the app is running is picked up the next time the app
writes one, or when the Events page is reloaded.

## Writing one without a text editor

The Events page creates and edits definitions itself, which is the shorter path for the common
case. Name an event and press Create; it is written to the folder above under a file named after
the name. With it selected, search the item catalog and press Add for each item it applies to, or
Remove to take one back out. Delete removes the file.

The page writes the same shape described below, so a definition it created can be edited by hand
afterwards, and one written by hand can be edited on the page. Rules are still a text editor's
job, but the page validates and previews them before they can affect planning or recommendations.

Recorded results are keyed by event id and item id together, so removing an item and adding it
back keeps what was recorded against it. Changing an event's `id` does orphan them.

## File shape

One event per file. `//` comments and trailing commas are allowed.

```jsonc
{
  "id": "allergy-2026",                  // required, unique across files; the player's recorded results
                                         // are keyed by it, so renaming it later orphans them
  "name": "Allergy event 2026",          // required, shown to the player
  "startUtc": "2026-09-01T00:00:00Z",    // optional ISO 8601 UTC, or null
  "endUtc": null,                        // optional; must not be earlier than startUtc
  "active": true,                        // required; false keeps the event listed but not in force
  "applicableItemIds": [],               // item ids as used by the app's item data (json.tarkov.dev ids)
  "rulesJson": "{\"effects\":[{\"type\":\"flea-availability\",\"enabled\":false}]}", // optional JSON text
  "provenance": {                        // optional; say where the item list came from
    "observedUtc": "2026-09-01T00:00:00Z",   // when you checked; defaults to the file's modified time
    "sourceUpdatedUtc": null,
    "reference": "https://example.invalid/patch-notes",  // the page or notes you worked from
    "confidence": { "value": 0.5 }           // 0 to 1; omitted or above 0.60 becomes 0.60
  }
}
```

A file is skipped without stopping the others when it is not valid JSON, is larger than 256 KB,
lacks `id`, `name` or `active`, has `endUtc` before `startUtc`, has a `rulesJson` that is not
JSON, or repeats an `id` already loaded from an earlier file (files load in name order). Nothing
is logged for a skipped file, so check these points first when an event does not appear.

## Typed effects

`rulesJson` is JSON text inside the definition. Its `effects` array may contain these objects:

- `trader-price-multiplier`: `traderId`, optional `traderName`, and `multiplier` greater than 0.
- `flea-availability`: `enabled` (`true` or `false`).
- `map-availability`: `mapId`, optional `mapName`, and `available`.
- `boss-spawn-multiplier`: `bossId`, optional `bossName` and `mapId`, and `multiplier`.
- `quest-availability-window`: `questId`, optional `questName`, and `startUtc`, `endUtc`, or both.

Extra properties are ignored for forward compatibility. Missing or invalid required properties and
unknown effect types are shown on Plan > Events with their JSON path. An invalid rule set never
affects a recommendation or the next-raid suggestion.

## The shipped example

`allergy-style.example.json` is disabled (`active: false`) and lists no items. It exists to show
the shape; nothing in it is an asserted fact about the game, and if copied into the events folder
it appears only as a disabled event. Do not enable it. Write a new file from reviewed information
about the current event, and verify the item list against that event before setting `active` to
true.
