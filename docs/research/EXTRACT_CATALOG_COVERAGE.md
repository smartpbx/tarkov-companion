# Extract catalog coverage sweep

Measured 2026-09-15 for issue #339.

## Result

The current `https://json.tarkov.dev/regular/maps` response omitted eleven current extracts on
seven of the thirteen canonical playable maps. Lighthouse accounted for five, including the
player-observed **Hideout Under the Landing Stage**. The primary response carried
`Last-Modified: Tue, 15 Sep 2026 08:24:16 GMT` and ETag
`"10df14edb3d8c1ea799fd0742ec4a373"` when reviewed.

| Map | Missing extract | Side | World X | Height | World Z |
| --- | --- | --- | ---: | ---: | ---: |
| Icebreaker | Helicopter | PMC | unavailable | unavailable | unavailable |
| Lighthouse | Hideout Under the Landing Stage | Scav | 133.068 | -0.467 | 286.842 |
| Lighthouse | Industrial Zone Gates | Scav | -152.56 | 12.25 | -794.1 |
| Lighthouse | Road to Military Base V-Ex | PMC | -328.951263 | 16.73 | -784.3581 |
| Lighthouse | Side Tunnel (Co-Op) | Shared | -68.3 | 6.83 | 318.11 |
| Lighthouse | Southern Road | PMC | -295.8 | 11.86 | 420.6 |
| Reserve | D-2 | PMC | -121.479065 | -17.0010071 | 172.24913 |
| Shoreline | Railway Bridge | PMC | -1029.29 | -60.79 | 307.59 |
| The Lab | Medical Block Elevator | Unknown | -112.423 | -3.10999966 | -343.986 |
| Terminal | Zubr Boat | PMC | unavailable | unavailable | unavailable |
| Woods | Friendship Bridge (Co-Op) | Shared | 93.17 | 16.57 | -843.98 |

`WorldPosition` is X, height, Z. The pinned coordinate files label those same axes as X, Z, Y,
so the table records the deliberate conversion rather than treating height as a map-plane
coordinate. No reviewed world coordinate was found for Helicopter or Zubr Boat. They are
therefore recognition definitions with “location unavailable,” never map markers. The pinned
Lab extraction table identifies Medical Block Elevator but does not publish a side, so the
catalog records `unknown` rather than inferring PMC.

## Full-map comparison

The canonical set comes from `the-hideout/tarkov-dev` `src/data/maps.json` at commit
[`9fd29f7f16e5d5408831d9be48274dcb8862de1d`](https://github.com/the-hideout/tarkov-dev/blob/9fd29f7f16e5d5408831d9be48274dcb8862de1d/src/data/maps.json).
The primary payload contained seventeen records because Factory, Ground Zero, and The Lab also
publish named variants. The comparison collapsed `night-factory` to Factory,
`ground-zero-21` and `ground-zero-tutorial` to Ground Zero, and `the-lab-dark` to The Lab. Virtual
`openworld` and `transits` catalog entries are not playable maps and were excluded.

Each current-list input is a permanent wiki revision rather than a mutable page:

| Canonical map | Reviewed list revision | Primary omissions |
| --- | --- | ---: |
| Customs | [355388](https://escapefromtarkov.fandom.com/wiki/Customs?oldid=355388) | 0 |
| Factory | [355345](https://escapefromtarkov.fandom.com/wiki/Factory?oldid=355345) | 0 |
| Ground Zero | [358836](https://escapefromtarkov.fandom.com/wiki/Ground_Zero?oldid=358836) | 0 |
| Icebreaker | [359388](https://escapefromtarkov.fandom.com/wiki/Icebreaker?oldid=359388) | 1 |
| Interchange | [360210](https://escapefromtarkov.fandom.com/wiki/Interchange?oldid=360210) | 0 |
| The Lab | [354844](https://escapefromtarkov.fandom.com/wiki/The_Lab?oldid=354844) | 1 |
| Labyrinth | [359378](https://escapefromtarkov.fandom.com/wiki/Labyrinth?oldid=359378) | 0 |
| Lighthouse | [359024](https://escapefromtarkov.fandom.com/wiki/Lighthouse?oldid=359024) | 5 |
| Reserve | [357731](https://escapefromtarkov.fandom.com/wiki/Reserve?oldid=357731) | 1 |
| Shoreline | [357755](https://escapefromtarkov.fandom.com/wiki/Shoreline?oldid=357755) | 1 |
| Streets of Tarkov | [353481](https://escapefromtarkov.fandom.com/wiki/Streets_of_Tarkov?oldid=353481) | 0 |
| Terminal | [359900](https://escapefromtarkov.fandom.com/wiki/Terminal?oldid=359900) | 1 |
| Woods | [355184](https://escapefromtarkov.fandom.com/wiki/Woods?oldid=355184) | 1 |

## Reproduction method

The bounded input to this comparison is retained at
`fixtures/extract-catalog/coverage-2026-09-15.normalized.json`. It records the source metadata,
all seventeen primary records after English-name expansion and duplicate-name collapse, the
thirteen permanent current-list revisions, the four explicit variant mappings, the pinned
canonical-catalog hash, the expected gaps, and the one punctuation-equivalence case. It is the
small factual comparison input, not a copy of any page, artwork, or raw API payload.

1. Fetch `https://json.tarkov.dev/regular/maps` and its English translation dictionary at
   `https://json.tarkov.dev/regular/maps_en`; retain the response metadata and the per-record
   English extract names in the normalized fixture above.
2. Build the thirteen-map set from the pinned canonical catalog, apply the four variant mappings
   above, and group primary extracts by canonical map.
3. Read the extraction tables from the thirteen pinned wiki revisions above. Normalize each name
   by lower-casing and retaining only letters and digits, then subtract primary identities within
   the same canonical map.
4. For unmatched names, inspect the static extract marker records in the relevant map
   configuration under `Plugin/Resources/Maps` at SPT-DynamicMaps commit
   [`4944764f5f6c42d152dca6bd1b5371c4f6212a9e`](https://github.com/acidphantasm/SPT-DynamicMaps/tree/4944764f5f6c42d152dca6bd1b5371c4f6212a9e/Plugin/Resources/Maps).
   Those configuration files last changed at `2026-03-14T07:48:43Z` and supply the nine plotted
   positions. Leave an unmatched row unplotted when that pinned source has no reviewed world
   coordinate.
5. Run `ReviewedExtractCatalogTests`. Its deterministic subtraction rebuilds the primary union
   per canonical map from the retained fixture and must produce exactly the eleven fixture gaps.
   The same test compares that result with the compiled map/name/side facts and ratchets the four
   variant mappings, thirteen reference revisions, pinned catalog hash, and punctuation collapse.
   The remaining tests ratchet nine finite positions, two absent positions, primary precedence,
   and recognition-only behavior.

The normalization also explains two rejected false positives. Shoreline's `Smuggler's Path
(Co-op)` was already represented by primary `Smugglers' Path (Co-op)` and differs only in
punctuation. A Labyrinth row in an earlier pass resulted from comparing the wrong map slug. Both
were excluded before the count above was recorded.

## Runtime rule

`json.tarkov.dev` remains the primary runtime source. The eleven reviewed facts are a bounded
compiled supplement, not a second network dependency. On each read the primary record wins by
map-scoped normalized name, so an upstream repair automatically suppresses the corresponding
supplement and cannot produce a duplicate marker. Nine facts have positions and reach both map
read boundaries. The two positionless facts reach screenshot recognition only.

The screenshot path also preserves a structurally labelled `EXFIL` row it cannot match. Such a
row is listed as offered with conservative evidence and no marker until a trusted position is
available. A speculative row must meet the candidate-confidence floor when the OCR provider
reports confidence, contain a name-like value of at most 64 characters, and fit within the
relay's sixteen-extract bound. Headers, status-only values, measurements, and one-character
readings remain unmatched. Unscored providers are retained at confidence 0.50 rather than being
misrepresented as zero. Trusted catalog matches take priority within the bound, and a
`catalog-gap:` observation can never highlight a static marker by substring. Unlabelled text and
ambiguous near-ties do not take that path.

Positionless reviewed definitions still reach the desktop list and retain their reviewed side.
They say “location unavailable,” are filtered for the player's side like positioned exits, and
never acquire an invented marker.

## Source and license review

Only nine static name/coordinate marker facts were transcribed from the pinned SPT-DynamicMaps
configuration files. The original repository's `Plugin/LICENSE` is MIT, copyright 2025 Michael
P. Starkweather, and its `map_and_data_credits.txt` says the marker data was datamined by that
program while crediting TarkovTracker/tarkovData, TarkovDev, and Shebuka for additional
information. Retained notice copies are at `LICENSES/SPT-DynamicMaps-MIT.txt` and
`LICENSES/SPT-DynamicMaps-map-and-data-credits.txt`.

The Creative Commons terms named by that credit file govern Shebuka SVG map layers. Tarkov
Companion copied no SVG, map layer, artwork, icon, marker asset, source code, wording, layout, or
live behavior from SPT-DynamicMaps. Helicopter and Zubr Boat are current-list facts only. Pinned
wiki revisions corroborate the other names and supported sides, but no wiki prose or assets are
redistributed. See ADR 0012 and `docs/LICENSING.md`.
