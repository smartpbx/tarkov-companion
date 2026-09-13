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

So **pair by position, never by order**: find the header, take the box beneath it, read the
caption inside that box. No caption means the slot is empty, which is information. Pairing lines
in reading order shifts by one at every empty slot and produces a plausible-looking wrong
loadout rather than an empty list, which is worse.

The **stash grid** on the right of the same screen carries fuller captions under each icon —
`MP855`, `Morphine`, `GoldenSta`, `Vaseline`, `Augment`, `Ibuprofen`, `SS0BPLF`, `VOG-25`. Still
truncated, but identifiable. If carried items are the goal, that is the better surface.

The full name exists on screen only in the hover tooltip, one item at a time
(`12/70 makeshift 50 BMG slug`), which is no use for a sweep.

| Element | Bounds (3840x1080) |
|---|---|
| Whole gear panel | x 0.26..0.56, y 0.13..0.76 |
| Left slot column | x 0.255..0.41 |
| Stash grid (captioned icons) | x 0.575..0.75 |
| Quick use strip (1-0 row, empty here) | x 0.40..0.60, y 0.87..0.93 |

The quick use strip **does** exist on the character screen, drawn as numbered empty boxes. That
is the same element reported absent in raid: it is a menu element, not a HUD one.
