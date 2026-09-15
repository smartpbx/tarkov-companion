# Extract catalog coverage sweep

Measured 2026-09-15 for issue #339.

## Result

The current `https://json.tarkov.dev/regular/maps` response omitted nine current extracts on
five supported maps. Lighthouse accounted for five, including the player-observed **Hideout
Under the Landing Stage**. The primary payload's response carried `Last-Modified: Tue, 15 Sep
2026 08:24:16 GMT` and ETag `"10df14edb3d8c1ea799fd0742ec4a373"` when reviewed.

| Map | Missing extract | Side | World X | Height | World Z |
| --- | --- | --- | ---: | ---: | ---: |
| Lighthouse | Hideout Under the Landing Stage | Scav | 133.068 | -0.467 | 286.842 |
| Lighthouse | Industrial Zone Gates | Scav | -152.56 | 12.25 | -794.1 |
| Lighthouse | Road to Military Base V-Ex | PMC | -328.951263 | 16.73 | -784.3581 |
| Lighthouse | Side Tunnel (Co-Op) | Shared | -68.3 | 6.83 | 318.11 |
| Lighthouse | Southern Road | PMC | -295.8 | 11.86 | 420.6 |
| Reserve | D-2 | PMC | -121.479065 | -17.0010071 | 172.24913 |
| Shoreline | Railway Bridge | PMC | -1029.29 | -60.79 | 307.59 |
| The Lab | Medical Block Elevator | PMC | -112.423 | -3.10999966 | -343.986 |
| Woods | Friendship Bridge (Co-Op) | Shared | 93.17 | 16.57 | -843.98 |

`WorldPosition` is X, height, Z. The pinned reference files label those same axes as X, Z, Y,
so the table records the deliberate conversion rather than treating height as a map-plane
coordinate.

## Method

1. Fetch the full regular-mode map payload and group its `extracts` by `normalizedName`.
2. Compare map-scoped extract names after removing case, punctuation, apostrophes, and spaces.
3. Review every unmatched extract marker in the pinned SPT Leaderboard map files at commit
   [`389e23571d7d6fe8c3da354f80fdca9cd14e9098`](https://github.com/SPT-Leaderboard/Website/tree/389e23571d7d6fe8c3da354f80fdca9cd14e9098/live-map/maps).
4. Retain only rows corroborated by the current EFT Wiki extract list for
   [Lighthouse](https://escapefromtarkov.fandom.com/wiki/Lighthouse),
   [Reserve](https://escapefromtarkov.fandom.com/wiki/Reserve),
   [Shoreline](https://escapefromtarkov.fandom.com/wiki/Shoreline),
   [The Lab](https://escapefromtarkov.fandom.com/wiki/The_Lab), and
   [Woods](https://escapefromtarkov.fandom.com/wiki/Woods).

The comparison initially produced two non-gaps. Shoreline's `Smuggler's Path (Co-op)` was
already represented by primary `Smugglers' Path (Co-op)` and differs only in punctuation. A
Labyrinth row resulted from comparing the wrong map slug. Both were excluded before the count
above was recorded.

## Runtime rule

`json.tarkov.dev` remains the primary runtime source. The nine reviewed facts are a bounded
compiled supplement, not a second network dependency. On each read the primary record wins by
map-scoped normalized name, so an upstream repair automatically suppresses the corresponding
supplement and cannot produce a duplicate marker.

The screenshot path also preserves a structurally labelled `EXFIL` row it cannot match. Such a
row is listed as offered with conservative evidence and no marker until a trusted position is
available. Unlabelled text and ambiguous near-ties do not take that path.

## Source and license review

Only the nine factual name/faction/coordinate records were transcribed from the pinned map
files; no code, artwork, wording, or layout was copied. That repository is MIT licensed and its
license is retained at `LICENSES/SPT-Leaderboard-MIT.txt`. The wiki pages corroborate current
names and sides; their prose and assets are not copied. See ADR 0012 and `docs/LICENSING.md`.
