# ADR 0007: Re-serving cached map artwork, and downloading item icons to fingerprint

Status: Proposed — 2026-09-14. Needs Clayton's acceptance before either half is built.

This is a reading of the published terms and of what this project already does. It is not
legal advice, and where the reading is uncertain it says so rather than rounding to a
convenient answer.

## Context

Two roadmap items are each blocked on the same kind of question, and `docs/LICENSING.md`
already says that question has to be answered before either can start:

> Any distribution of cached, converted, or modified artwork must be separately reviewed for
> attribution, NonCommercial, ShareAlike, and upstream anti-cheat compliance.

**The tablet drawing the real map** (Ambitious 5). #29 asked for the group map on a second
screen; what shipped is a labelled grid, and the page says so. The relay is the tablet's only
origin by design — the tablet is a browser on a phone with no access to the desktop's cache —
so drawing the real map means the relay serving `maps.json` and the SVGs it names. ADR 0002
records the artwork as "downloaded/cached for local use only", and a relay re-serving it to
other devices is a step past that wording.

**Item icon fingerprinting** (Ambitious 3). `EFT_SCREENSHOT_FACTS.md` closes the caption route
and measures the stash grid as exact. `SkiaPerceptualIconMatcher` exists and `IIconMatcher` is
not registered; `item_icon_fingerprints` was dropped in 0007 as never written. Matching a
grid cell against a known icon means downloading tarkov.dev's item images. `LICENSING.md` says
nothing about item images at all.

## What the terms actually say

**Map artwork** is `the-hideout/tarkov-dev-svg-maps`, CC BY-NC-SA 4.0, with an additional
explicit prohibition on use in cheating or unfair-advantage software — radar, ESP, cheat-client
maps, automation, pixel bots — carrying a stated revocation clause.

CC BY-NC-SA 4.0 grants the right to "copy and redistribute the material in any medium or
format" and to adapt it, for NonCommercial purposes, under Attribution and ShareAlike. So
**redistribution is granted by the licence itself.** "Local use only" in ADR 0002 was this
project's own conservative posture, decided when nothing here served anything to anybody; it
was never a limit the licence imposed. That is the finding this ADR turns on, and it is worth
stating plainly because the previous wording reads as though the licence forbade it.

**Item images** are a different case and a weaker one. `the-hideout/tarkov-dev` is MIT, which
covers the site and its configuration; the item renders it serves are derived from Battlestate
Games' own assets, and neither MIT nor any tarkov.dev statement purports to license those to
anybody. Nobody in this chain is in a position to grant rights in them.

## Decision

### A. The relay may re-serve map artwork to the tablet, on four conditions

1. **Attribution travels with the bytes.** The tablet page shows the author, a link to the
   source, and a link to CC BY-NC-SA 4.0, on the page that draws the map rather than behind a
   menu. The desktop map already does this and the tablet has no excuse not to.
2. **Unmodified bytes, or ShareAlike attaches.** Serving the original SVG is redistribution and
   nothing more. Rasterising, recolouring or recombining it is an adaptation, and an adaptation
   has to be offered under the same licence. Serve the original; if the tablet ever needs a
   raster, that raster is CC BY-NC-SA 4.0 and has to say so.
3. **NonCommercial, and it stays that way.** No charge, no advertising, no bundling into
   anything sold. This is an internal tool among friends and the relay is one box; if that ever
   changes, this decision is void and needs redoing before the change ships.
4. **The anti-cheat prohibition is restated where it can be read.** `SAFETY.md` line 45 already
   forbids ESP, radar and reading live players out of the game, and the tablet draws only what
   the group's own members published from their own screenshots — the same data the desktop map
   already draws, on a second screen. That is compliance, not a coincidence, and the revocation
   clause is why it has to stay written down rather than assumed.

Two things this does **not** change. Artwork is still never bundled into a source or release
archive — `scripts/package-windows.sh` rejects map-cache metadata before packaging and that
rule stands. And the relay caches and serves; it does not convert, crop or re-encode.

### B. Item icons may be downloaded and fingerprinted locally, and neither the images nor the
### fingerprints may be published

Downloading an image to compute a perceptual hash of it, and keeping the hash, is the same
posture this project already takes with map artwork: a local cache, retaining the upstream URI,
retrieval time, content hash and attribution, never redistributed and never bundled. On that
footing it is no worse than what already ships.

Three limits, and the third is the one that matters:

1. **The images stay local.** They are cached on the player's machine, the way map artwork is,
   and the relay never serves them. The relay's catalog mirror carries the item *data*; adding
   the images to it would be redistributing artwork nobody in this chain can license.
2. **The fingerprints stay local too.** A table of perceptual hashes of somebody else's artwork
   is derived from it, and whether a 12-bit dHash is a copy in any legal sense is genuinely
   unsettled. This project does not need to find out: the fingerprints are computed on the
   machine that uses them, from a cache on that machine, and are never published, shared
   through the relay, or committed. If `item_icon_fingerprints` comes back, it comes back as a
   local cache with a reader in the same commit, which is the rule #174 established.
3. **The empirical gate is still shut.** None of the above says the technique works. The open
   question is whether tarkov.dev's grid renders match the in-game render — translucent ground,
   the found-in-raid badge, rotation, multi-cell items — inside a dHash cutoff at 61 px, and
   that is a measurement against real frames, not an argument. Nothing is built until one
   screenshot's cells have been hashed against downloaded icons for the items visible in it and
   the separation, or the lack of it, is written into `EFT_SCREENSHOT_FACTS.md`.

## Consequences

ADR 0002's "local use only" is narrowed to what it was actually protecting: no bundling into
releases, and no commercial distribution. Re-serving cached artwork to the group's own devices,
attributed and unmodified, is within CC BY-NC-SA 4.0 and is now allowed.

The tablet's real-map work (Ambitious 5) is unblocked on the licence question and still blocked
on Now 11 and Now 12, which it depends on.

Icon fingerprinting (Ambitious 3) is unblocked on the licence question and still blocked on the
measurement, which needs the screenshot corpus and therefore a machine that has it.

`docs/LICENSING.md` gains a paragraph pointing here, so the next person asking "may we serve
this" finds the answer rather than the question.

If the project ever becomes commercial, or ever draws anything about a player who is not in the
group, both halves of this decision lapse and have to be made again.
