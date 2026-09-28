# Test checklist items

The dev-mode test checklist reads every `*.json` file in this folder, sorted by file name, and
shows their items in that order. Each item is one feature a person can walk through and mark
works, broken, needs work or skipped.

## Files

- `00-checklist.json` tests the checklist page itself.
- `10-` to `90-` hold one area each (Raid map, Raid, Intel, Plan, Team, Debrief, Scanning, Setup,
  App and updates, Sound and Ask). Split a file or add one freely; keep the two-digit prefix so
  the order stays deliberate.

## Adding an item

Add an object to the file's `items` array, schema version 1:

```json
{
  "id": "raid.map.follow",
  "area": "Raid map",
  "feature": "Follow",
  "needs": ["game", "in-raid"],
  "steps": ["Start a raid", "Press the game's screenshot key"],
  "expect": "The map centres on the new position at the Follow zoom.",
  "goTo": "#/raid",
  "flag": "now-panel",
  "refs": ["#286"]
}
```

- `id`: lowercase, dot-separated, unique across every file in this folder. The loader rejects a
  duplicate.
- `needs`: any of `game`, `in-raid`, `pmc`, `scav`, `squad`, `tablet`, `relay`,
  `stash-screenshot`, `loot-screenshot`, `flea-screenshot`, `second-pc`, `windows`.
- `steps`: what a player does, in the game as normal or in the companion, using the labels the
  screen shows. Never a tool used on the game.
- `expect`: what correct looks like, specific enough to decide pass or fail. An estimate is
  called an estimate; nothing is described as live detection.
- `goTo`: a shell route (`#/raid`, `#/intel/ammo`, ...). `flag`: the feature flag the item sits
  behind. `refs`: the PRs or issues that shipped it.

## Ids are never reused

Results are saved per id in `Config/test-checklist-results.json`. If a feature is removed, delete
its item and retire the id for good. A new item with an old id would inherit a verdict somebody
recorded about something else. Changing an item's wording keeps its id; changing what it tests
needs a new one.
