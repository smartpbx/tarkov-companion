# Outstanding work

Things Clayton asked for that have no issue of their own. This file exists because a long
session displaced the queue once already: an installer request landed mid-turn, cost five CI
rounds and a near data-loss, and the rest of the list went unworked without anyone noticing.
Written down, it cannot quietly stop existing.

The larger pieces of work live in
[issues](https://github.com/smartpbx/tarkov-companion/issues) instead, and
[#139](https://github.com/smartpbx/tarkov-companion/issues/139) is the roadmap those were cut
from. Ordered by how much it changes what he sees. Delete an entry when it ships.

## Map

- [ ] **Clicking an extract should show a picture of it.** What it looks like when you arrive
      matters more than which exit it is. The conditions half of this shipped — the card says
      whether a switch has to be thrown first and what it costs — and the picture still has no
      source. tarkov.dev publishes none.
- [ ] **Interior maps for buildings.** The data is there. `maps.json` carries 67 named X/Z
      regions across five maps — Reserve 32, Customs 28, Ground Zero 3, Interchange 2,
      Shoreline 2 — with 44 distinct names, and they are the names people use: `dorms`, `D2`,
      `white king`, `mall`, `west wing`, `zb-1011`, `switch basement`. They are already parsed
      into `MapCatalogBounds.Description` and already used: `MapAreaName.Locate` is what lets
      the map say which building you are standing in. What is missing is artwork for the inside
      of one, which no feed publishes, so this is the same blocked-on-a-source problem as the
      extract picture above rather than a modelling problem.
- [ ] **3D view and 3D tracking.** Now
      [#151](https://github.com/smartpbx/tarkov-companion/issues/151), which carries what Geo
      actually built and what it would take here.

## Scanning

- [ ] **Retire the GDI capture stack.** The global hotkey is gone — the window's bindings only
      fire when the companion has focus, which is correct beside a fullscreen game — but
      `GdiScreenCaptureService` is still registered and `ScanUseCase` still calls
      `IScreenCaptureService.CaptureAsync`. Screenshot-driven scans are the path that matters
      and they read a file. Roughly 380 lines, once nothing needs them.
- [ ] **Diagnose why recognition returns Unknown.** The diagnostic line reports how many text
      lines were read, at what frame size, and how the contexts scored. Still needs one
      screenshot of an extract list and one of the stash to settle whether it is the text
      engine or the anchors. Blocked on those two files and nothing else.

## Interface

- [ ] **Assert the CI page gallery.** It photographs every page and only fails when a window
      does not appear, so nothing about the pixels is checked. The ragged sidebar shipped
      because CI took the picture and nobody looked. Commit per-page baselines so the next
      design pass gets the review this one never had. This is the one that would have caught
      [#245](https://github.com/smartpbx/tarkov-companion/issues/245) a week earlier.
- [ ] **The rest of the completeness critic's findings.** Twenty-seven originally. Two of the
      worst are done — raw enum values no longer reach the player, and the navigation glyphs
      are drawn in-repo rather than picked out of unrelated Unicode blocks — and
      [#245](https://github.com/smartpbx/tarkov-companion/issues/245) took the prose. What is
      left is truncation, clipping, and states the design never considered.

## Done since this file was last true

Kept briefly, because the list above is only trustworthy if the things that left it are
visible.

- Map labels that overlapped and clipped, the floor following the player's height, the stacked
  floor view, exits drawn per faction, and exit conditions on the card.
- A public hostname for the relay, and the relay mirroring the game catalog so five clients no
  longer each pull the same several megabytes from upstream. The schema half of that is
  [#119](https://github.com/smartpbx/tarkov-companion/issues/119).
- The relay's second screen, its operator panel, and problem reports that become issues.
