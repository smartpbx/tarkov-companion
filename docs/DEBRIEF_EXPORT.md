# Debrief export, schema version 3

Debrief exports raid history as CSV or JSON from the buttons above the raid list. Every fact
carries the kind of evidence behind it, so a figure the companion worked out is never mistaken for
one the game wrote, and a field the player typed is never mistaken for either.

## The four kinds

| Kind | Meaning | Example |
| --- | --- | --- |
| `observed` | Something the game wrote: a log line, a screenshot's own name. | The map, the start time, a normal end time. |
| `inferred` | The companion worked it out; it did not see it. | The mode (the active profile's setting when the raid was first seen), a recognised item, an end time from a raid the companion found closed on restart. |
| `estimated` | A bound or a price, not a reading. | A scan's value (a market price at scan time), a duration whose end was inferred, the distance covered. |
| `manual` | The player typed it. | An outcome or notes saved from Debrief; kills and the value brought out. |

An empty field has no source. In CSV the source cell is empty; in JSON it is `null`. Absent is not
"observed nothing".

How each raid field is classified is in `RaidFactRules` (`Core/Domain/Raids/RaidFacts.cs`). The game
records no outcome, so a normal raid end writes none: the only writers are the companion closing a
raid it never saw end (fixed text, an inferred end) and a player's correction, which is stored as a
`correction` event so it stays manual even if the player types the companion's own words.

## CSV

One row per raid, first row is the header. Read columns by name. Version 2 kept version 1's eight
columns first and unchanged; version 3 does the same to version 2's seventeen.

| Column | Meaning |
| --- | --- |
| `id`, `profile_id`, `map_id`, `mode` | As version 1. |
| `start_local`, `end_local` | The player's own clock, `yyyy-MM-dd HH:mm:ss` (`LocalTime.SortableSeconds`), read by a spreadsheet as a date-time. Empty while the raid is in progress. The database keeps UTC; only what leaves the machine is converted. |
| `outcome`, `notes` | As version 1. |
| `schema_version` | `3`. |
| `map_source`, `mode_source`, `start_source`, `end_source`, `outcome_source`, `notes_source` | One of the four kinds, or empty. |
| `scans` | How many scans were recorded during the raid. |
| `scans_recognised` | How many of them named an item. |
| `pmc_kills`, `scav_kills`, `boss_kills`, `value_roubles` | Typed by hand on the raid in Debrief. Empty until entered; a raid where the player entered `0` PMC kills reads `0`, not empty — zero is an answer, not "not asked". |
| `pmc_kills_source`, `scav_kills_source`, `boss_kills_source`, `value_roubles_source` | `manual` where the field is entered, empty where it is not. Always `manual` where present: nothing else writes these fields. |

## JSON

```json
{
  "schemaVersion": 3,
  "exportedUtc": "2026-09-19T12:00:00+00:00",
  "raids": [
    {
      "id": "…", "profileId": "…", "mapId": "customs", "mode": "Regular",
      "started": "2026-09-19T08:03:39-04:00", "ended": "2026-09-19T08:27:40-04:00", "outcome": "Survived", "notes": null,
      "pmcKills": 2, "scavKills": 1, "bossKills": 0, "valueRoubles": 450000,
      "sources": { "map": "observed", "mode": "inferred", "started": "observed",
                   "ended": "observed", "outcome": "manual", "notes": null,
                   "pmcKills": "manual", "scavKills": "manual", "bossKills": "manual", "valueRoubles": "manual" },
      "scans": [
        { "observedUtc": "…", "available": true, "recognised": true,
          "itemId": "…", "itemName": "…", "itemSource": "inferred", "confidence": 0.93,
          "valueRoubles": 12000, "valuePerSlotRoubles": 12000, "valueSource": "estimated",
          "recommendation": "Take" }
      ]
    }
  ]
}
```

Version 1 was a bare array of raids with the first eight fields, `startedUtc`/`endedUtc` included.
Version 2 is an envelope — the one incompatible change — and the two time fields are renamed
`started`/`ended` and carry the player's own offset (`LocalTime.Iso`, e.g. `…T08:03:39-04:00`)
rather than a bare UTC instant, the same rule the rest of the product follows: nothing user-visible
prints a UTC clock. Every other field keeps its version 1 name. `exportedUtc`, the envelope's own
timestamp, stays UTC like the database and the logs. A scan that found nothing has `itemSource` and
`valueSource` of `null`. No scan carries a claim of value carried out of the raid.

Version 3 adds `pmcKills`, `scavKills`, `bossKills` and `valueRoubles` on the raid, and their
sources (always `manual` where present) on `sources`. A field nobody typed is `null`, not `0`:
zero is a real answer the player entered, absence is that nobody was asked yet. Every version 2
field keeps its name and place.

A scan the player marks wrong keeps its original event plus the correction in local history, but
is omitted from scan counts and the exported scan list.

## Not in version 3

Offered extracts (the `extracts` events an extract-list screenshot writes), the extract used
(`extract-used`, typed or picked in Debrief) and archive state (`archive`) are recorded as raid
events and shown in Debrief's Stats view, but are not exported yet; the export includes archived
raids. Planned-versus-actual routes are not exported either. Counts here
are per raid. Per-map totals for the manual fields are shown in Debrief's own "By map" panel, not
in the export, which stays per-raid.
