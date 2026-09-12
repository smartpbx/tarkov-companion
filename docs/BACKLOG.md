# Outstanding work

Requested by Clayton and not yet done. This file exists because a long session
displaced the queue once already: an installer request landed mid-turn, cost five CI
rounds and a near data-loss, and the rest of the list went unworked without anyone
noticing. Written down, it cannot quietly stop existing.

Ordered by how much it changes what he sees. Delete an entry when it ships.

## Map

- [ ] **Labels overlap and are unreadable.** Reported with a screenshot: "Sniper
      Roadblock (scav)" sits across the marker above it and the "Transit to Shoreline"
      label below it; "Dorms V-Ex (pmc)" sits across its own disc. Names are placed at a
      fixed offset under the disc with no awareness of their neighbours, so they collide
      wherever markers are close. Needs real label de-collision, not a visibility
      threshold.
      Note: the "(scav)" and "(pmc)" suffixes in those labels are now redundant, because
      the disc colour and the glyph already say it. Removing the suffix shortens every
      extract label and removes some collisions for free.
- [ ] **Clicking an extract should show a picture of it and its conditions.** Which exit
      it is matters less than what it looks like when you arrive and what it requires.
      tarkov.dev carries extract conditions; pictures need a source.
- [ ] **Floor follows the player's height.** `MapPresentationService.SelectFloor(variant,
      position)` already exists, is already correct, and has no caller anywhere. Needs a
      dead band so it cannot flap at a stairwell, and must not fight a manual choice.
      Hazard found in review: `_selectionLoad` is a single shared CancellationTokenSource
      that floor load and variant load both own, so firing this per screenshot makes an
      existing race routine.
- [ ] **Interior maps for buildings.** `MapLayerExtent` already carries X/Z bounds as well
      as height, so a layer covering one building is expressible. Unverified whether the
      catalog actually publishes any; check before promising it.
- [ ] **3D view and 3D tracking.** Avalonia has no scene graph. The panel's recommendation
      is a tilted 2.5D floor stack drawn through Skia `DrawVertices`, fed by the same
      scene, with the flat view as an instant fallback, rather than starting from a blank
      GL context. The friend's Blender pipeline is public domain and portable.

## Scanning

- [ ] **Remove the Ctrl+Alt+S hotkey and its GDI capture stack.** There is no recognition
      capability unique to it; both paths converge on the same reader. It survives because
      screenshot-driven scans do not reach the interface: the result is published to a
      holder that nothing reads. Wire that up first, then the hotkey and roughly 380 lines
      can go.
- [ ] **Diagnose why recognition returns Unknown.** The new diagnostic line reports how
      many text lines were read, at what frame size, and how the contexts scored. Needs one
      screenshot of an extract list and one of the stash to settle whether it is the text
      engine or the anchors.

## Interface

- [ ] **The completeness critic's findings.** Twenty-seven, unstarted. The worst: raw enum
      values reach the player, so the status readout prints "InRaid" and
      "LauncherOrGameDetected" mid-raid. Also truncation, clipping, states the design never
      considered, and controls whose meaning was only ever carried by prose that has now
      been deleted.
- [ ] **Navigation glyphs are from unrelated Unicode blocks.** They resolve to different
      fallback fonts, so each renders at its own size, weight and baseline. A coherent set
      drawn in-repo rather than characters picked by eye.
- [ ] **Assert the CI page gallery.** It photographs every page and only fails when a
      window does not appear, so nothing about the pixels is checked. The ragged sidebar
      shipped because CI took the picture and nobody looked. Commit per-page baselines so
      the next design pass gets the review this one never had.

## Server

- [ ] **Cloudflare public hostname for the group relay.** Blocked on an API token, or on
      Clayton adding it in the dashboard pointing at the container. Suggested
      `tarkov.mannerow.net`.
- [ ] **Catalog publishing from the server.** The panel's one server feature worth having
      first: the server syncs tarkov.dev once and publishes a versioned snapshot, so a
      shape change upstream is fixed once rather than in every client. Must keep the
      direct-to-upstream path as a fallback, or the server stops being an optimisation and
      becomes a dependency.
