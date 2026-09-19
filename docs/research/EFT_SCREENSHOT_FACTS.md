# What an EFT screenshot actually contains

Measured on Clayton's own installation, 282 screenshots, September 2026. Everything here came
off the pixels; nothing is inferred from how other games lay out a HUD. The first attempt at
this feature reasoned from a 1920x1080 layout and looked in the wrong corner of the screen, so
the rule for this file is that a claim without a measurement behind it does not belong in it.

## Frames

| Fact | Value |
|---|---|
| Screenshots sampled | 282 |
| Resolution | 3840x1080, every single one |
| In-raid (coordinates in the filename) | 249 |
| Menu or character screen (no coordinates) | 33 |

One resolution, and it is 32:9. **A fraction of the frame's width is not a stable way to
describe anything in the HUD.** The overlay is anchored to the frame's true left edge, so the
same element sits at half the fractional width on 32:9 that it does on 16:9. Everything below is
therefore given in pixels at 1080 tall, and in the code as units of frame *height* from the left
and bottom edges.

## The HUD is in the bottom LEFT, and is often not drawn

| Element | Pixels (3840x1080) | In frame heights from left/bottom |
|---|---|---|
| Body silhouette | x 55..180, y 837..997 | x 0.051..0.167, up 0.077..0.225 |
| Upper bar | x 51..196, y 1027..1029 | x 0.047..0.181, up 0.047..0.049 |
| Lower bar | x 51..196, y 1036..1038 | x 0.047..0.181, up 0.039..0.041 |
| Posture scale | x ≈ 30 | |
| Version string (bottom centre) | x ≈ 0.25..0.32 of width, y ≈ 0.98 | |

**The game fades the HUD out when nothing has changed recently.** Across all 261 in-raid
screenshots checked:

| | Count | Share |
|---|---|---|
| HUD drawn | 226 | 86.6% |
| HUD absent entirely | 35 | **13.4%** |

A screenshot with no HUD in it is ordinary, not a failure. Anything reading the HUD has to treat
absence as its own answer: a companion that reported "empty" or "healthy" off a frame that never
contained a bar would be inventing a number the player would believe.

## There are two bars, and they are different colours

A vertical slice at x=120 on a HUD-present frame:

```
y 1026   22,22,22      border
y 1027   43,129,151    BAR ONE, blue over green
y 1028   34,114,131
y 1029   22,94,105
y 1030   14,14,14      gap
y 1031   44,46,45      grey separator
y 1032   dark
y 1036   43,151,129    BAR TWO, green over blue
y 1037   32,128,106
y 1038   22,105,83
y 1039   17,17,17      border
```

One colour predicate catches both and reports them as one element. They are separated by which
of blue and green leads. Which meter each one is has not been established, so the code names
them by the colour a player can see rather than guessing at "stamina".

**There is no track behind the bars.** A horizontal walk along y=1028 goes scene, then
34,114,131 uniformly from x=52 to x=196, then scene. The colour goes straight from bar to
background with nothing between, so there is no empty portion to measure a percentage against.
Bar length is only meaningful against the longest ever seen, and the code says so on screen
rather than printing a percentage with an invented denominator.

**Do not read fill from a pixel count.** An early measurement gave 427 pixels on one frame and
438 on another and took it for a difference in fill. Both bars ran x 51..196 exactly; the counts
differed only because antialiased edge rows fall in and out of any colour test. Length comes
from the extent.

## Limb health is not readable from these screenshots

This is the part worth reading before anybody tries it again.

The silhouette is **translucent**. Sampled over its own bounds on a frame taken in grass it
returns 2911 near-unique dark greens, the commonest at 23,30,13 with 0.3% of samples. On a frame
taken facing a sand bank the entire figure reads orange, evenly, head to boots. It is the scene
showing through.

The outline is **too dim to detect**, which is the more serious problem. Over grass:

| | Luminance |
|---|---|
| Brightest pixel in the whole silhouette region | 83 (RGB 81,88,64) |
| 99th percentile | 64 |
| Median | 31 |
| Pixels with R, G and B all above 150 | **zero** |

Over a sand bank, which is the brightest background in the set, the brightest pixel reaches
147 and there are still zero pixels with all three channels above 150.

A classifier of the usual shape — white outline means healthy, red means hurt, absent means the
limb is gone — was run against these frames exactly as written:

```
IsWhite(r,g,b) => r > 150 && g > 150 && b > 150 && |r-b| < 60 && |r-g| < 60
IsRed(r,g,b)   => r > 110 && r-g > 45 && r-b > 45
bright = white + red
bright < max(12, area * 0.004)  -> destroyed
red / bright >= 0.25            -> hurt
otherwise                       -> ok
```

| Frame | IsWhite | IsRed | Verdict | Truth |
|---|---|---|---|---|
| Sand bank, HUD present | 0 | 14940 / 20000 | **hurt** | healthy |
| Grass, HUD present | 0 | 0 | **destroyed** | healthy |
| HUD absent | 0 | 0 | **destroyed** | no HUD at all |

It never returns "ok" on any screenshot on this machine, it reports an injury whenever the
player looks at something warm-coloured, and it cannot tell "the game drew nothing" from "the
leg is gone" on the 13% of frames with no HUD. Sand at 163,113,68 gives r−g = 50 and r−b = 95,
clear of both thresholds; three quarters of the region passes and the ratio is exactly 1.0.

Two separate mechanisms, same conclusion: the fill is the scene, and the outline never reaches
the brightness any classifier needs.

**What the code does instead:** it measures the brightest pixel in the silhouette region and
refuses when it is below 150, saying what it found and what it would need. Whether the outline
is dim because of a graphics setting, a game version or a monitor is not something a screenshot
can answer, and somebody else's screenshots may well be legible — which is exactly why the
answer is "this frame's outline peaks at 83" rather than "limb health does not work".

Still wanted: **one screenshot of a genuine injury, taken over a dark background.** None of the
261 in-raid screenshots has a damaged or blacked limb. Five had any reddish pixel at all in the
silhouette region: one was the sand bank, and the other four were one or two pixels of scene
noise.

## There is no quick bar

The 1-0 slot strip is not drawn in any screenshot in the set. The only interface along the
bottom centre is a version string, roughly `1.1.5.0.47242 | CYRSMM | PvP S`, at about x
0.25..0.32 of the width and y 0.98. Whether it is bound off, hidden by a setting or only drawn
transiently is unknown, but it is not a route to what the player is carrying on this machine.

**The character screen is the route.** A menu screenshot run through the bright-half OCR
preparation reads OVERALL, CUSTOMIZATION, ACHIEVEMENTS, HEALTH, SKILLS, MAP, TASKS, GEAR, BACK
and SEARCH cleanly, and the loadout is legible on it.

## A note on the older menu screenshots

32 of the 33 menu screenshots are from 2024 and are OneDrive cloud-only placeholders. Reading
one costs a download. `ScreenshotRetention.IsCloudOnly` exists for this.

## The extract panel is top-right, and every row has a slot label

Measured on a Woods raid whose scan matched one exit out of five.

| | Value |
|---|---|
| Panel bounds | x 0.850..0.995, y 0.005..0.50 of a 3840x1080 frame |
| Rows on that screenshot | 10 (5 exits, 4 transits, 1 header) |
| Header | a bright green bar with black text |
| Timer column | right-aligned near x 0.975, **its own OCR line**, not appended to the name |

The vertical extent grows with the number of exits, so crop generously downwards.

**Read the panel on its own, untouched.** Probing the whole 3840x1080 frame returned 240 lines
of noise with the exit names buried; cropping to the panel and probing that returned 16 lines
with every name legible. The bright-half preparation that suits a full frame *hurts* here — the
names are drawn lighter and smaller than the slot labels beside them, and thin pale text is what
a threshold eats first. "As captured" read it best.

**Every row begins with a slot label on the same line as the name.** Verbatim OCR:

```
Find an extraction point
0:12:28
EXFILO1 Friendship Bridge (Co-Op)
22:22: 2
Ss
EXFIL@2 ZB-214
EXFIL@3 Bridge V-Ex
22:22:22
```

What the screen actually draws:

```
[green bar]  Find an extraction point            0:12:28
EXFIL01  Friendship Bridge (Co-Op)
EXFIL02  ZB-014                                  ??:??:??
EXFIL03  Bridge V-Ex                             ??:??:??
EXFIL04  Outskirts
EXFIL05  Power Line Passage (Flare)
TRANSIT01  Transit to Factory
TRANSIT02  Transit to Reserve
TRANSIT03  Transit to Lighthouse
TRANSIT01  Transit to Customs
```

That label is eight to ten characters of dead weight against a catalog name that has none, and
the shorter the real name the more it dominates a similarity score:

| Row | Name chars | Prefix chars |
|---|---|---|
| Power Line Passage (Flare) | 26 | 8 |
| Friendship Bridge (Co-Op) | 25 | 8 |
| Bridge V-Ex | 11 | 8 |
| Outskirts | 9 | 8 |
| ZB-014 | 6 | 8 |

Power Line Passage is the longest name on the screen and was the only row to clear the
threshold. That is the whole explanation for "it only found one extract".

**The zero in the slot number is usually not a zero.** It reads as a letter `O` or an at-sign:
`EXFILO1`, `EXFIL@2`, `EXFIL@3`, `EXFIL@1`, `EXFILO2`. A pattern anchored on `\d` fails on most
rows.

**Do not trust the timer column.** Where the game prints `??:??:??` for an unknown time, the
question marks OCR as twos and it comes back as `22:22:22` — a plausible-looking number for an
exit twenty-two hours away. The raid clock's one-hour cap happens to reject it, which is luck
rather than design.

**TRANSIT rows are a different kind.** They are ways to another map, drawn in orange, and absent
from every extract catalog. Matching them against one produces nothing but lines that failed.

**`ZB-014` read as `ZB-214`** on this screen, and as `zB-014` only at 3x. Six characters with one
wrong is not something a threshold can rescue, and loosening one far enough to catch it would
match it to whatever else is nearest.

## Carried items: the gear slots are not the route, the stash grid is

On the character screen each gear slot is a box with a bright white uppercase header above it —
ON SLING, HOLSTER, ON BACK, SHEATH, EARPIECE, HEADWEAR, FACE COVER, ARMBAND, BODY ARMOR,
EYEWEAR, DOGTAG, TACTICAL RIG, POCKETS, SPECIAL SLOTS, BACKPACK, POUCH. Those headers are
high-contrast and OCR cleanly.

The contents do not.

- An **empty** slot draws a ghost outline on a hatched background and **no text at all**.
- A **filled** slot draws the icon plus a short caption in its top-right corner, heavily
  abbreviated: a hatchet reads `KATT`. Four characters is not enough to identify an item.

So **pair by position, never by order**. Two independent reasons, and the second is the stronger:

1. An empty slot contributes a header and no item, so every subsequent pairing shifts by one.
2. **OCR reading order is not stable.** On the same crop, the same engine returned the headers
   in a different order under two preparations — "as captured" gave HEADWEAR, FACE COVER,
   EARPIECE; bright-half gave EARPIECE, HEADWEAR, FACE COVER. Sparse-text mode does not
   guarantee reading order and it does not survive a threshold change.

An order-based parser therefore produces a plausible-looking wrong loadout on a *full* character
screen with nothing empty at all, which is worse than an empty list.

One more shape to know: **the chevron on each header comes back as its own line**. The stream is
header, chevron, header, chevron, so anything counting lines is out by a factor of two.
"BODY ARMOR" reading as "Boby ARMOR" was the only header misread.

The **stash grid** on the right of the same screen carries fuller captions under each icon —
`MP855`, `Morphine`, `GoldenSta`, `Vaseline`, `Augment`, `Ibuprofen`, `SS0BPLF`, `VOG-25`. Still
truncated, but identifiable. If carried items are the goal, that is the better surface.

The full name exists on screen only in the hover tooltip, one item at a time
(`12/70 makeshift 50 BMG slug`), which is no use for a sweep.

### The stash captions do not survive OCR either

Measured against the truth, read by eye at 3x, on one band of the grid:

| On screen | What OCR returned across six preparations |
|---|---|
| `TSS AP` | `TaSgAP`, `TS5,AP`, `TSS,AP` |
| `M21` | `Mel`, `M1` |
| `TT 855A1` | `WL S55At`, `TT B2501`, `TT B2SAl`, `TT 855A]` |
| `DBX95` | `DBx9s`, `DBx95`, `DA#95`, `DAxSS` |
| `Disk` | `Disk` — the only one right, and only in three of six |

Two structural problems, worse than the character errors:

- **Adjacent captions merge.** `Mel TT @55Al1` is cell five's caption and cell six's caption on
  one OCR line. Two different items, one line; a line-based parser produces a single item that
  is neither.
- **The captions are not item names.** `PP`, `FMJ`, `TSS AP`, `M21`, `DBX95` are ammunition type
  codes with no calibre. Two adjacent cells both read `VOG-25` and two others both read `M7290`,
  so a caption does not uniquely identify a cell even read perfectly.

**So neither surface gives identity.** What is reliable on this screen is the gear slot headers
and whether a slot is occupied — "he has a primary, a holster weapon and a sheath; headwear, rig
and backpack are empty" — which is a real answer and much less than it first looked like.

### The stash grid's geometry, which is exact

Autocorrelation on the brightness profile, lags 30 to 110: the strongest peak is at **63** for
columns and **63** for rows, so the cells are square. On a 1080-tall frame that is 0.0583 of the
height — and 63 is exactly 7/120 of 1080, which is why the code treats it as a function of
height rather than a constant.

Ten consecutive vertical grid lines were detected at x = 2287, 2350, 2413 … 2854, and **every one
satisfies x mod 63 = 19** — not approximately, exactly. Nothing drifts across the panel, which is
what makes per-cell cropping safe. Rows are noisier because icons interrupt the lines; the
horizontal phase is about `y = 18 + 63k` and is good to a pixel or two.

Inside a cell: the caption is **top-right**, roughly the top third; the stack count is
**bottom-right**, roughly the bottom quarter; some cells carry a small circular badge, presumably
found-in-raid. Cropping the caption band alone keeps "50" out of the same reading as the label.

**What the stash grid's lines are made of** (nine 3840x1080 menu screenshots, 2026-09-18, measured
through `StashLuminancePlane`). An item's border is one pixel, teal-tinted, 25 to 45 luminance
levels over the pixels on both sides (85,108,105 between 56,73,50 and 55,62,70), unbroken along
the side and drawn on the lattice line itself. The line between two empty cells is 15 over a
purple hatch that alternates by about 6 from pixel to pixel (36,27,41 / 42,33,46). Grid showing
through the transparent parts of a large item's art is 8 to 10. Armour and backpacks sit on a pale
tile (about 90) whose own border measures 1 to 5; its edge is a step of 50 to 65 to the dark tile
next door. The viewport has its own top line across the whole panel two or three pixels above the
first row (0.66 to 0.89 of the width reads as a line, against 0.1 to 0.2 for the header text above
it), and the panel's outer frame runs up into that header, so the frame is not the viewport. The
scroll is not row-aligned at the bottom of the stash: the first row can be the cut one. A hover
tooltip covers about eight cells and one outer edge.

**Detect the phase, do not hardcode it.** The panel moves with the window, so a hardcoded origin
is wrong the first time somebody plays windowed. All of the above came from one screenshot, at
one window size, on one machine.

Per-cell cropping fixes the merging and **will not fix the character errors** — `TSS AP` failed
from a single cell with nothing adjacent to blame. It is worth doing mainly so that the next
comparison tests the engine rather than the merging.

| Element | Bounds (3840x1080) |
|---|---|
| Whole gear panel | x 0.26..0.56, y 0.13..0.76 |
| Left slot column | x 0.255..0.41 |
| Stash grid (captioned icons) | x 0.575..0.75 |
| Quick use strip (1-0 row, empty here) | x 0.40..0.60, y 0.87..0.93 |

The quick use strip **does** exist on the character screen, drawn as numbered empty boxes. That
is the same element reported absent in raid: it is a menu element, not a HUD one.

### What nine real stash screenshots settled (2026-09-18)

Nine menu screenshots from one minute on Clayton's machine, all 3840x1080: the character screen
with the stash scrolled to six positions, one with a hover tooltip, and two with an item case
opened in a window over it. They are hideout screens. No in-raid container is among them.

| Fact | Measured |
|---|---|
| Cell pitch | **63.0** pixels on both axes in all nine, on every grid on the screen |
| Stash panel | ten columns, x = 2225 to 2855, in every frame |
| Item and cell borders | one bright pixel, at x = 2225 + 63k, so **x mod 63 = 20** |
| Row phase | differs per frame (y = 79, 64, 127, 331 ...): the stash scrolls by pixels, not by rows |
| A case window | its own lattice at the same pitch and another phase (1518,149 and 1604,109) |
| Interface | drawn in the centred 1920 pixels of the 3840; the outer quarters are backdrop |

The paragraph above gives the column phase as 19. That measurement found the dark gap beside
each line by autocorrelation. Icon fingerprints settle which one is the cell edge: a lattice at
2224 named 3 to 7 items a frame and the same lattice at 2225 named 11 to 13.

Several grids share the screen at that one pitch, each at its own phase: gear slots, pockets,
special slots, the backpack, the stash, and any open case window. A lattice search that may
re-anchor on each line it finds walks from one into the next. `ContainerGridDetector` returned
one lattice across several panels in 8 of these 9 frames until it was held to the known pitch.

What is drawn over an icon: the caption top-right, in a font close enough to json.tarkov.dev's
renders that two armbands differing only in band colour were told apart; a found-in-raid tick
bottom-right on most items; a stack count or durability figure bottom-right (`30/30`,
`1295/1500`); a hatched tint on tagged containers. A hover tooltip covered eight cells in one
frame, and the game's own "screenshot saved" toast sits bottom-right of the interface in some.

A weapon is drawn at the size of its build, not its catalog size: an M9A3 with a magazine is
2x2 against a catalog 2x1, so references filtered by catalog shape never include it.

### The carried panel, the gear slots and the vitals strip on the same nine frames (2026-09-19)

Measured by `RealCharacterScreenMeasurementTests`, which reports and never asserts, and skips
without the private folder. Coordinates are for 3840x1080, where the interface is a 1920-wide
panel centred in the frame.

**The nine frames are one sample of all of this.** They were taken in one minute while the
stash was scrolled. The carried panel did not move and the loadout did not change: the
backpack's grid lines fall on the same pixels in frames (0) to (5), and (6) to (8) have a
tooltip or a case window over that part of the screen. Nine screenshots are one backpack and
one loadout, six times over.

**The carried panel is not a grid. It is a scrolling column of separate grids.** From the top:
the tactical rig (a 2x2 slot for the rig itself, then its pouches, each its own small grid with
gaps between them), POCKETS, SPECIAL SLOTS, BACKPACK (a 2x2 slot for the bag, then its grid),
and below that whatever the scroll position hides. Each has a text header. A rig and a backpack
are the same shape to a line detector, a slot with a grid to its right, so "the panel with the
most line" cannot say which one it found. The header can.

The backpack in these frames is a 3-column sling bag. Its vertical lines are at x = 1748, 1812,
1875 and 1939, the same 63 to 64 pixel pitch as the stash. Its horizontal lines are at y = 525,
589, 652, 715, 778, 841 and 904, and then the quick-use bar begins at y = 949. That is six whole
rows and **45 of the seventh row's 63 pixels**: the bag has seven rows and the panel cuts the
last one. So a carried grid read from a screenshot can be short of the real bag, which a fit
claimed inside the visible rows survives and a "no room" claim does not.

**Gear-slot occupancy is a bright tail, not a mean.** Eleven slots: earpiece, headwear, face
cover, armband, body armor, eyewear, dogtag, on sling, holster, on back, sheath. An empty slot
is a dark hatch with a faint placeholder glyph. In this loadout three are empty.

| | Brightest pixel | 99th percentile | Share of pixels over 90 |
|---|---|---|---|
| Empty (eyewear, holster, on back) | 56 to 64 | 46 to 51 | 0 |
| Occupied (the other eight) | 209 to 255 | 132 to 210 | 0.03 to 0.35 |

The mean does not separate them: the sling slot, holding a black submachine gun on a black
tile, averages 16.1, under all three empty slots (17.0 to 20.0). The gap in the tail is wide,
and it is eight occupied slots and three empty ones from one loadout, so it is a measurement
and not a threshold. Nothing has been built on it.

**The Gear tab draws no limb health.** There is a dim figure behind the slots and no per-limb
values; those are on the HEALTH tab, and none of the nine frames is the HEALTH tab.

**The vitals strip is bright.** Bottom-left of the Gear tab: weight, total health, hydration and
energy, each with its maximum and a rate (`440/440`, `66/100`, `26/110`). Its brightest pixel is
224 and 1,866 of its 30,740 pixels are over 150. The in-raid HUD silhouette peaks at 83. So this
text is of the legible kind, unlike the HUD. Whether OCR reads it is not measured: the packaged
Tesseract provider runs on Windows only, and these figures were taken on the Linux build host.

All 360 labelled items sit in exactly the catalog's footprint. None is rotated and none is drawn
at a different size, so these frames cannot measure a rotated item at all.
