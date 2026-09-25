# Tarkov Companion v3 UI concepts

These seven renders are concepts for epic #712 ("the companion knows what you are doing"). They
tell one evening on Customs for a PMC PvP profile with squadmates Geo, Riley and Sam, and they
are the design bar for the Now panel work. They are not runtime assets. The screen list and exact
content come from the V3 screen brief; colours, type scale, radii and spacing are the V2 dark
tokens (`src/TarkovCompanion.App/Themes/V2/`).

Unlike the v2 set, these are not generated images. Each screen is hand-written HTML and CSS in
`src/`, rendered to PNG at its exact size (1920×1080, or 1280×800 for the tablet) with headless
Chromium. That keeps them editable when the brief changes. To re-render one:

```bash
chrome-headless-shell --headless --no-sandbox --hide-scrollbars --window-size=1920,1080 \
  --screenshot=v3-raid-now.png file://$PWD/src/v3-raid-now.html
```

Inter must be installed locally; the app bundles it through `Avalonia.Fonts.Inter`. Nothing is
fetched from the network. The Customs map is a stylised plan drawn from scratch in `src/v3.js`.
Positions are approximate, and no tarkov.dev or Shebuka map artwork is copied or bundled (see
`docs/LICENSING.md`). Every modelled or predicted fact is labelled as such, squad positions carry
their age, and nothing depicts enemies or is drawn over the game.

## Screens

- **`v3-preraid-brief.png`: the brief during matching.** The log named Customs while the
  player was matching, so the Raid workspace shows the brief with no clicks. It holds the gold
  objective route with pins A and B, the likely spawn side as a hatched band labelled
  PREDICTED, the quests here with the keys each needs, and a bring list that warns about a
  missing key while leaving is still possible. The PMC extracts and the squad state from the
  relay come last. A "because" strip explains the switch and offers Undo. In V2 the player picks
  the map and sees the Raid plan card stack saying "Not in raid".
- **`v3-raid-now.png`: the Now panel in mid raid.** The right-hand panel replaces V2's 16-card
  "Raid plan" with five blocks in human units: NOW, YOU, SQUAD, NEXT and LAST SCAN. NOW holds the
  only clock, because the top bar no longer carries one. YOU says where you are in words from a
  12-second-old screenshot and names the nearest offered exit. SQUAD gives area, distance and age
  instead of V2's coordinates and "floor unknown". The reference cards move to the More drawer.
  The map keeps a single "MODELLED TRAFFIC · NOT LIVE" chip.
- **`v3-raid-late-loot.png`: late raid with a loot verdict.** The NOW block turns amber and
  gives a leave-by time for the chosen extract, which V2 never showed. A screenshot of an opened
  toolbox gets a verdict in the panel within about a second: Take 2, Swap 1, Leave 1, with ₽ per
  square, the reason for each keep and the spoken sentence. The panel says the image was not kept.
  V2 jumped to the Loot page and needed an armed intent. A squad ping pulses the map edge on
  Riley's side.
- **`v3-post-raid-outcome.png`: after the raid.** When the log reports the match over, the
  panel asks one question with three large buttons. A photographed summary screen can answer it
  instead. Below it are a recap and a hand-in reminder, and the next raid is already suggested.
  The extract used is labelled as inferred from the last position. V2's Debrief showed "Outcome:
  Not recorded" and needed a manual correction.
- **`v3-screenshot-answer.png`: screenshot to answer between raids.** With nothing armed,
  three screenshots route themselves: a trader screen opens Intel on Mechanic with the buys that
  beat the flea at a 5% fee. A TASKS burst becomes one "Sync 4 changes?" banner that applies
  nothing until Confirm. The stash page merges into the snapshot, and an unrecognised frame is a
  quiet row asking what it was. V2 needed Alt+Shift+C or "Read as…" and had no trader handler.
- **`v3-tablet-now.png`: the tablet companion.** The paired tablet shows the same Situation as
  the desktop at touch sizes: the map with 48 px floor and zoom targets beside the Now column,
  with 56 px squad rows that each have a Ping button. The desktop's "because" line is mirrored at
  the bottom. The V2 tablet page was the map and review sheets with no clock, squad list or
  objective.
- **`v3-squad-ready.png`: squad, shared plan and ready check.** The Team workspace combines
  what V2 split across separate cards and a manual Ready toggle. It shows tonight's shared
  quests counted per quest, with the 1.1 shared-completion planner. The ready check is fed by
  each member's own Loadout check. It also shows the session plan, a map thumbnail with the shared
  route and each member's marks, and a footnote that only what each companion shares is shown.
