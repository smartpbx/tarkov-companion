# Debrief export, schema version 2

Debrief exports raid history as CSV or JSON from the buttons above the raid list. Every fact
carries the kind of evidence behind it, so a figure the companion worked out is never mistaken for
one the game wrote, and a field the player typed is never mistaken for either.

## The four kinds

| Kind | Meaning | Example |
| --- | --- | --- |
| `observed` | Something the game wrote: a log line, a screenshot's own name. | The map, the start time, a normal end time. |
| `inferred` | The companion worked it out; it did not see it. | The mode (the active profile's setting when the raid was first seen), a recognised item, an end time from a raid the companion found closed on restart. |
| `estimated` | A bound or a price, not a reading. | A scan's value (a market price at scan time), a duration whose end was inferred, the distance covered. |
| `manual` | The player typed it. | An outcome or notes saved from Debrief. |

An empty field has no source. In CSV the source cell is empty; in JSON it is `null`. Absent is not
"observed nothing".

How each raid field is classified is in `RaidFactRules` (`Core/Domain/Raids/RaidFacts.cs`). The game
records no outcome, so a normal raid end writes none: the only writers are the companion closing a
raid it never saw end (fixed text, an inferred end) and a player's correction, which is stored as a
`correction` event so it stays manual even if the player types the companion's own words.

## CSV

One row per raid, first row is the header. Read columns by name. Version 2 keeps version 1's eight
columns first and unchanged.

| Column | Meaning |
| --- | --- |
| `id`, `profile_id`, `map_id`, `mode` | As version 1. |
| `start_utc`, `end_utc` | UTC, ISO 8601 round-trip. Empty while the raid is in progress. |
| `outcome`, `notes` | As version 1. |
| `schema_version` | `2`. |
| `map_source`, `mode_source`, `start_source`, `end_source`, `outcome_source`, `notes_source` | One of the four kinds, or empty. |
| `scans` | How many scans were recorded during the raid. |
| `scans_recognised` | How many of them named an item. |

## JSON

```json
{
  "schemaVersion": 2,
  "exportedUtc": "2026-09-19T12:00:00+00:00",
  "raids": [
    {
      "id": "…", "profileId": "…", "mapId": "customs", "mode": "Regular",
      "startedUtc": "…", "endedUtc": "…", "outcome": "Survived", "notes": null,
      "sources": { "map": "observed", "mode": "inferred", "started": "observed",
                   "ended": "observed", "outcome": "manual", "notes": null },
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

Version 1 was a bare array of raids with the first eight fields. Version 2 is an envelope, which is
the one incompatible change; the eight fields keep their names. A scan that found nothing has
`itemSource` and `valueSource` of `null`. No scan carries a claim of value carried out of the raid.

## Not in version 2

Offered extracts, per-map coverage as a numerator and denominator, a recommendation version on each
scan, and planned-versus-actual routes are not recorded yet, so they are not exported. Counts here
are per raid.
