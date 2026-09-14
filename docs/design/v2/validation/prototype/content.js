/*
 * Shared sample content for both navigation storyboards (issue #265).
 *
 * Both variants read this one object so a difference in a session can only come from navigation
 * and placement, never from one variant being given easier numbers. Every figure here is
 * illustrative: names, prices, sizes, counts, times, confidence values and model figures are
 * sample values chosen to reconcile with each other, not catalog facts or measurements.
 *
 * The arithmetic is deliberate and is repeated in ../journeys.md. If a number changes here, change
 * it there too, or the moderator's expected answers stop matching the screen.
 */
window.STORYBOARD_CONTENT = {
  sampleNotice:
    'Sample data. Names, prices, sizes, counts, times and model figures are illustrative.',
  localTime: '18:42',
  // J6 starts after the raid, so its header must not show a clock earlier than the raid's end.
  postRaid: { localTime: '18:56', state: 'Not in raid · last raid ended 18:52:30 (game log)' },
  profile: {
    none: 'No profile chosen',
    chosen: 'Sample profile · PvP · wipe started 2026-06-17'
  },

  readiness: [
    { id: 'logs', label: 'Game log folder', status: 'ok', statusText: 'Found',
      detail: 'Watching the folder EFT writes its logs to. Checked 18:40.', action: 'Change folder' },
    { id: 'screens', label: 'Screenshot folder', status: 'ok', statusText: 'Found',
      detail: 'New screenshots are read. Analysis never moves or deletes them. Checked 18:40.',
      action: 'Change folder' },
    { id: 'ocr', label: 'Text recognition', status: 'ok', statusText: 'Available',
      detail: 'Windows recogniser answered the self-test at 18:40.', action: 'Run self-test' },
    { id: 'data', label: 'Game data', status: 'ok', statusText: 'Synced 12 min ago',
      detail: 'From json.tarkov.dev, cached for offline use.', action: 'Check now' },
    { id: 'profile', label: 'Profile and wipe', status: 'action', statusText: 'Not chosen',
      detail: 'Quest and hideout advice needs to know whose progress it is.', action: 'Choose profile' },
    { id: 'team', label: 'Team and tablet', status: 'optional', statusText: 'Off (optional)',
      detail: 'Nothing is shared while this is off.', action: 'Set up later' }
  ],

  privacy: [
    'Screenshot folder cleanup: off. The companion does not move or delete your EFT screenshots.',
    'Capture analysis: the decoded image is discarded after analysis. The screenshot file stays in your EFT folder.',
    'Debug capture: off. No image is kept.'
  ],

  intents: [
    { id: 'loot', label: 'Loot decision', hint: 'One screenshot with the container and your carried inventory open.' },
    { id: 'stash', label: 'Full stash', hint: 'A guided series of screenshots, scrolling one screen at a time.' },
    { id: 'ammo', label: 'Ammo', hint: 'Open ammo boxes or magazines first.' },
    { id: 'keys', label: 'Keys', hint: 'Open the key tool or keychain first.' },
    { id: 'quest', label: 'Quest items', hint: 'Show the items you want checked against quests.' },
    { id: 'extracts', label: 'Map and extracts', hint: 'Open the extract list with the O key in game yourself.' },
    { id: 'health', label: 'Health and character', hint: 'Open the character screen.' },
    { id: 'flea', label: 'Flea listings you opened', hint: 'Open the flea page yourself. Only the rows you can see are read.' },
    { id: 'auto', label: 'Auto-detect', hint: 'The companion decides what the screenshot shows.' }
  ],

  captureStages: ['File seen', 'File finished writing', 'Decoded', 'Context', 'Grid and regions',
    'Item matches', 'Profile lookup', 'Recommendation', 'Complete'],

  raid: {
    state: 'In raid',
    map: 'Customs',
    side: 'PMC',
    stateSource: 'Game log line at 18:30:12',
    elapsed: '12:30 since raid start (game log)',
    remaining: 'Unknown: extract list not photographed this raid',
    position: 'From your screenshot at 18:41:07 (1 min old)',
    model: {
      label: 'Modelled traffic · not live',
      source: 'Sample historical dataset (storyboard placeholder)',
      dataThrough: '2026-09-12',
      generated: '2026-09-13 04:00 UTC',
      coverage: '1,284 raids (sample); 6 of 9 zones covered',
      confidence: 'Medium (sample calibration 0.72)',
      version: 'traffic-sample-v0',
      why: 'Shown because the Traffic layer is on for Customs, PMC, raid minutes 10 to 20.'
    },
    zones: [
      { id: 'dorms', name: 'Dorms', level: 'High', x: 175, y: 120, w: 60, h: 40 },
      { id: 'bridge', name: 'Main bridge', level: 'High', x: 270, y: 150, w: 50, h: 30 },
      { id: 'construction', name: 'Construction', level: 'Medium', x: 205, y: 180, w: 60, h: 35 },
      { id: 'bigred', name: 'Big Red', level: 'Medium', x: 320, y: 90, w: 55, h: 35 },
      { id: 'wh4', name: 'Warehouse 4', level: 'Low', x: 95, y: 150, w: 55, h: 30 },
      { id: 'stronghold', name: 'Stronghold', level: 'Low', x: 105, y: 200, w: 60, h: 30 },
      { id: 'trailer', name: 'Trailer park', level: 'Not covered', x: 320, y: 30, w: 60, h: 30 },
      { id: 'gas', name: 'Old gas station', level: 'Not covered', x: 190, y: 40, w: 70, h: 30 },
      { id: 'factory', name: 'Factory far corner', level: 'Not covered', x: 20, y: 60, w: 70, h: 30 }
    ],
    extracts: [
      { name: 'ZB-1011', x: 40, y: 150, status: 'Catalog: possible. Not yet confirmed this raid.' },
      { name: 'RUAF Roadblock', x: 380, y: 140, status: 'Catalog: possible. Not yet confirmed this raid.' },
      { name: 'Old Road Gate', x: 200, y: 245, status: 'Catalog: conditional (sample condition). Not confirmed.' }
    ],
    routes: [
      { id: 'lower', name: 'Lower modelled contact', estimate: '14 to 18 min (modelled)', extract: 'ZB-1011',
        via: ['Stronghold', 'Warehouse 4', 'ZB-1011'],
        tradeoff: 'Avoids Dorms and Main bridge (both High). Longest walk.',
        points: '235,215 135,215 122,165 40,150' },
      { id: 'fast', name: 'Faster, higher modelled contact', estimate: '12 to 16 min (modelled)', extract: 'RUAF Roadblock',
        via: ['Dorms', 'Main bridge', 'RUAF Roadblock'],
        tradeoff: 'Crosses Dorms and Main bridge, both High in minutes 10 to 20.',
        points: '235,215 205,140 295,165 380,140' }
    ],
    you: { x: 235, y: 215 }
  },

  /*
   * Loot decision arithmetic, all sample values:
   *   Container 6 x 4 = 24 squares, 6 items using 2+1+1+2+1+2 = 9 squares.
   *   Backpack 4 x 4 = 16 squares; carried 1+2+1+4+2+2 = 12 used; 4 free as one 2 x 2 block.
   *   TAKE fuel conditioner (1x2) + Virtex (1x1) + bolts (1x1) = 4 squares -> 0 free.
   *   SWAP electric drill (2x1) into the Wires slot (2x1): 58,000 - 24,000 = +34,000 net est.
   *   LEAVE Crickent; REVIEW military cable (low-confidence match). 3+1+1+1 = 6 of 6 items.
   *   Carried after the moves: 6 - Wires + 4 = 9 items. Priced: fuel conditioner 110,000 + Virtex 72,000
   *   + bolts 18,000 + electric drill 58,000 = 258,000 flea net est. (Debrief timeline).
   */
  loot: {
    detected: 'Loot container and carried backpack',
    detectConfidence: '0.94',
    screenshot: '18:41:07',
    analysedIn: '1.4 s (sample)',
    valueBand: 'Take when at least ₽25,000 per square (your sample setting)',
    container: { cols: 6, rows: 4, used: 9 },
    backpack: { cols: 4, rows: 4, used: 12, free: 4, freeShape: 'one 2×2 block' },
    carried: [
      { name: 'IFAK', size: '1×1', squares: 1, note: 'Protected: medical' },
      { name: 'Water bottle', size: '1×2', squares: 2, note: 'Protected: food and water' },
      { name: '5.45×39 BP rounds', size: '1×1', squares: 1, note: 'Protected: loadout ammo' },
      { name: 'Spare headset', size: '2×2', squares: 4, note: 'Protected: pinned by you' },
      { name: 'Gas analyzer', size: '1×2', squares: 2, note: 'Protected: current quest (sample)' },
      { name: 'Wires', size: '2×1', squares: 2, note: 'Lowest unprotected: ₽12,000 per square net est.' }
    ],
    decisions: [
      { item: 'fuel-conditioner', decision: 'TAKE', reasons: ['Current quest needs 1 more (Sample quest A)', 'Fits the free 2×2 block'],
        change: 'Would become LEAVE if the quest is already handed in.' },
      { item: 'virtex', decision: 'TAKE', reasons: ['Future quest needs 1 (Sample quest D)', 'Hideout needs 1', 'Above your value band'],
        change: 'Would stay TAKE on value alone.' },
      { item: 'bolts', decision: 'TAKE', reasons: ['Hideout still needs 6', 'Below your value band, taken for the hideout need'],
        change: 'Would become LEAVE if hideout need is turned off.' },
      { item: 'electric-drill', decision: 'SWAP', reasons: ['Above your value band', 'No free space left after the takes', 'Replace Wires (2×1, lowest unprotected carried item)'],
        swapOut: 'wires', gain: 34000, change: 'Would become LEAVE if Wires were protected.' },
      { item: 'military-cable', decision: 'REVIEW', reasons: ['Match confidence 0.58: Military cable or Power cord', 'Below your value band either way'],
        change: 'Correct the match to see the final decision.' },
      { item: 'crickent', decision: 'LEAVE', reasons: ['Below your value band', 'No quest, hideout or pin needs it'],
        change: 'Would become TAKE if you pin it.' }
    ]
  },

  items: {
    'fuel-conditioner': { name: 'Fuel conditioner', type: 'Item', size: '1×2', squares: 2, gross: 122000, fee: 12000, net: 110000, trader: 64000, traderName: 'Sample trader', priceAge: '12 min', confidence: '0.97',
      needs: ['Current quest: 1 more (Sample quest A)'], owned: 'Unknown: no stash scan covers it' },
    virtex: { name: 'Virtex processor', type: 'Item', size: '1×1', squares: 1, gross: 80000, fee: 8000, net: 72000, trader: 41000, traderName: 'Sample trader', priceAge: '12 min', confidence: '0.95',
      needs: ['Future quest: 1 (Sample quest D)', 'Hideout: 1 (sample station level 2)'], owned: 'Observed 0 in stash scan at 18:20 (partial coverage)' },
    bolts: { name: 'Bolts', type: 'Item', size: '1×1', squares: 1, gross: 20000, fee: 2000, net: 18000, trader: 9000, traderName: 'Sample trader', priceAge: '12 min', confidence: '0.91',
      needs: ['Hideout: 6 still needed (sample)'], owned: 'Observed 2 in stash scan at 18:20 (partial coverage)' },
    'electric-drill': { name: 'Electric drill', type: 'Item', size: '2×1', squares: 2, gross: 64000, fee: 6000, net: 58000, trader: 30000, traderName: 'Sample trader', priceAge: '12 min', confidence: '0.93',
      needs: [], owned: 'Unknown: no stash scan covers it' },
    'military-cable': { name: 'Military cable', type: 'Item', size: '2×1', squares: 2, gross: 25000, fee: 3000, net: 22000, trader: 11000, traderName: 'Sample trader', priceAge: '12 min', confidence: '0.58',
      alternative: 'Power cord', needs: [], owned: 'Unknown: no stash scan covers it' },
    'power-cord': { name: 'Power cord', type: 'Item', size: '1×2', squares: 2, gross: 18000, fee: 1800, net: 16200, trader: 9000, traderName: 'Sample trader', priceAge: '12 min', confidence: 'Manual correction',
      needs: [], owned: 'Unknown: no stash scan covers it' },
    crickent: { name: 'Crickent lighter', type: 'Item', size: '1×1', squares: 1, gross: 7000, fee: 1000, net: 6000, trader: 3000, traderName: 'Sample trader', priceAge: '12 min', confidence: '0.96',
      needs: [], owned: 'Unknown: no stash scan covers it' },
    wires: { name: 'Wires', type: 'Item', size: '2×1', squares: 2, gross: 27000, fee: 3000, net: 24000, trader: 12000, traderName: 'Sample trader', priceAge: '12 min', confidence: '0.94',
      needs: [], owned: 'Carried: 1 (Loot decision at 18:41)' },
    'graphics-card': { name: 'Graphics card', type: 'Item', size: '2×1', squares: 2, gross: 890000, fee: 88000, net: 802000, trader: 124000, traderName: 'Sample trader', priceAge: '12 min', confidence: '0.98',
      needs: ['Hideout: 2 for sample station level 1'], owned: 'Observed 3 in stash scan at 18:20 (partial coverage)', advice: 'Keep 2, sell extras' },
    'dorm-key': { name: 'Dorm room 214 key', type: 'Key', size: '1×1', squares: 1, gross: 220000, fee: 22000, net: 198000, trader: 18000, traderName: 'Sample trader', priceAge: '3 h (stale)', confidence: '0.99',
      needs: ['Current plan: Customs progression'], owned: 'Observed 1 in stash scan at 18:20 (partial coverage)' }
  },

  /*
   * Stash session arithmetic, all sample values:
   *   Capture 1 rows 1-24: 46 stacks. Capture 2 rows 19-42: 45 stacks, 7 duplicates merged -> +38.
   *   Total 84 = Keep 20 + Sell 31 + Use soon 14 + Review 19.  Review 19 = 2 keys + 6 counts + 11 identities.
   *   Coverage 42 of 68 rows.  Capture 3 (rows 37-60) adds 38 stacks with 5 duplicates -> +33 = 117.
   *   After capture 3: Keep 27 + Sell 45 + Use soon 19 + Review 26 = 117; Review 26 = 3 keys + 8 counts + 15 identities.
   */
  stash: {
    rowsTotal: 68,
    steps: [
      { n: 1, rows: 'Rows 1 to 24', stacks: 46, duplicates: 0, added: 46 },
      { n: 2, rows: 'Rows 19 to 42', stacks: 45, duplicates: 7, added: 38 },
      { n: 3, rows: 'Rows 37 to 60', stacks: 38, duplicates: 5, added: 33 }
    ],
    observedLayouts: [
      { rows: 'Rows 1 to 18', cells: 'Observed in capture 1', source: 'Screenshot 1 at 18:20:02' },
      { rows: 'Rows 19 to 24', cells: 'Observed in captures 1 and 2; overlap merged', source: 'Screenshots 1 and 2' },
      { rows: 'Rows 25 to 42', cells: 'Observed in capture 2', source: 'Screenshot 2 at 18:20:31' },
      { rows: 'Rows 43 to 68', cells: 'Unknown: not covered by the current snapshot', source: 'No screenshot observed' }
    ],
    observedLayoutsAfterThird: [
      { rows: 'Rows 1 to 18', cells: 'Observed in capture 1', source: 'Screenshot 1 at 18:20:02' },
      { rows: 'Rows 19 to 24', cells: 'Observed in captures 1 and 2; overlap merged', source: 'Screenshots 1 and 2' },
      { rows: 'Rows 25 to 42', cells: 'Observed in capture 2', source: 'Screenshot 2 at 18:20:31' },
      { rows: 'Rows 43 to 60', cells: 'Observed in capture 3', source: 'Screenshot 3 at 18:21:04' },
      { rows: 'Rows 61 to 68', cells: 'Unknown: not covered by the current snapshot', source: 'No screenshot observed' }
    ],
    snapshots: [
      { captures: 2, rowsCovered: 42, total: 84, keep: 20, sell: 31, useSoon: 14, review: 19,
        reviewParts: '2 keys, 6 unreadable stack counts, 11 uncertain identities',
        keys: '9 key stacks seen: 7 identified, 2 need review', sellValue: '₽1,240,000 flea net est. (29 of 31 Sell stacks priced)',
        next: 'Scroll down one screen so row 37 is at the top, then take a screenshot.' },
      { captures: 3, rowsCovered: 60, total: 117, keep: 27, sell: 45, useSoon: 19, review: 26,
        reviewParts: '3 keys, 8 unreadable stack counts, 15 uncertain identities',
        keys: '12 key stacks seen: 9 identified, 3 need review', sellValue: '₽1,910,000 flea net est. (42 of 45 Sell stacks priced)',
        next: 'Scroll to the bottom so rows 61 to 68 are visible, then take a screenshot.' }
    ],
    organise: [
      'Put ammo together by calibre in one case.',
      'Move quest items for Sample quests A to D beside each other.',
      'Queue 14 duplicate low-value stacks for selling.',
      'Check the uncertain keys before selling any key.'
    ]
  },

  plan: {
    bundles: [
      { id: 'customs', name: 'Customs progression', summary: '3 quests · 3 objectives · 1 extract' },
      { id: 'woods', name: 'Woods supply run', summary: '2 quests · 2 objectives · 1 extract' },
      { id: 'hideout', name: 'Hideout priority', summary: '6 items to find' }
    ],
    objectives: [
      'Find the package at Big Red (Sample quest A)',
      'Mark the fuel tanks near Construction (Sample quest B)',
      'Check room 214 in Dorms (Sample quest C)'
    ],
    extract: 'ZB-1011 (catalog: possible; confirm in raid)',
    requirements: [
      { name: 'Dorm room 214 key', status: 'Confirmed', source: 'Seen in stash scan at 18:20 (42 of 68 rows covered)' },
      { name: 'MS2000 Marker ×2', status: 'Confirmed', source: 'Entered by you on 2026-09-13' },
      { name: 'Food and water', status: 'Unknown', source: 'Not checked by any scan or entry' }
    ],
    requirementSummary: '2 confirmed · 1 unknown',
    route: {
      estimate: '24 to 29 min (modelled)',
      confidence: 'Medium (sample)',
      tradeoff: 'Enters Dorms, modelled High in minutes 10 to 20, because objective 3 is there. Arriving after minute 25 is modelled Low.',
      steps: ['Big Red', 'Construction', 'Dorms', 'ZB-1011'],
      points: '347,107 235,197 205,140 40,150'
    }
  },

  team: {
    members: [
      { name: 'You', role: 'Owner', device: 'This desktop', freshness: 'Now', stale: false },
      { name: 'Birch (sample)', role: 'Member', device: 'Desktop', freshness: 'Updated 8 s ago', stale: false },
      { name: 'Moth (sample)', role: 'Member', device: 'Desktop', freshness: 'Stale: last update 2 min ago', stale: true }
    ],
    roles: [
      { role: 'Owner', can: 'Invite, revoke devices, clear all marks, everything a Member can do' },
      { role: 'Member', can: 'Add marks, move and delete their own marks, share objectives' },
      { role: 'Observer', can: 'View only' }
    ],
    device: { name: 'Your tablet (sample)', relation: 'A paired device of You. Not a squad member.',
      scope: 'Controls what your desktop companion shows. Never the game.', expires: 'Session expires in 6 days' },
    marks: [
      { kind: 'Waypoint', label: 'Meet at Warehouse 4', author: 'Birch (sample) · desktop', scope: 'Team', age: '6 min ago', ttl: 'Until cleared' },
      { kind: 'Ping', label: 'Look here: Main bridge', author: 'You · tablet', scope: 'My paired devices', age: '20 s ago', ttl: 'Expires in 25 s' }
    ],
    places: ['Big Red', 'Construction', 'Dorms', 'Main bridge', 'Stronghold', 'Warehouse 4', 'ZB-1011', 'RUAF Roadblock']
  },

  debrief: {
    raids: [
      { id: 'r3', when: '2026-09-14 18:30', map: 'Customs', side: 'PMC', outcome: 'Survived', outcomeSource: 'Entered by you' },
      { id: 'r2', when: '2026-09-13 21:05', map: 'Woods', side: 'Scav', outcome: 'Not recorded', outcomeSource: 'No entry' },
      { id: 'r1', when: '2026-09-13 20:10', map: 'Customs', side: 'PMC', outcome: 'Killed', outcomeSource: 'Entered by you' }
    ],
    timeline: [
      { time: '18:30:12', text: 'Raid started: Customs, PMC', kind: 'Observed', source: 'Game log' },
      { time: '18:36:40', text: 'Position near Stronghold', kind: 'Observed', source: 'Your screenshot filename' },
      { time: '18:41:07', text: 'Loot decision: TAKE 3 · SWAP 1 · LEAVE 1 · REVIEW 1', kind: 'Estimated', source: 'Capture analysis' },
      { time: '18:41:07', text: 'Estimated value taken this raid ₽258,000 flea net est. (4 of 9 carried items priced after the moves)', kind: 'Estimated', source: 'Capture analysis; not extracted value' },
      { time: '18:52:30', text: 'Raid ended', kind: 'Observed', source: 'Game log' },
      { time: '18:52:30', text: 'Extracted at ZB-1011', kind: 'Inferred', source: 'Last position and raid end; not confirmed by the game' },
      { time: '18:55:00', text: 'Outcome: Survived', kind: 'Manual', source: 'Entered by you' }
    ],
    prediction: 'Traffic prediction shown at raid time: traffic-sample-v0, data through 2026-09-12. Preserved as shown, not re-scored with a newer model.'
  },

  setupSections: ['Get ready', 'Game and profile', 'Recognition', 'Data', 'Team and devices', 'Updates',
    'Privacy', 'Appearance', 'Accessibility', 'Diagnostics'],

  /*
   * Abbreviated from ../state-matrix.md, which is authoritative. Each cell: what is still usable,
   * what the recovery control says, and what a screen reader hears. Success carries no banner.
   */
  states: {
    raid: {
      empty: ['No raid seen in the game log since the companion started.', 'Choose map', 'Raid: no raid active.'],
      loading: ['Extracts and objectives are listed while map tiles load.', 'Use list view', 'Loading Customs map.'],
      offline: ['Cached tiles, cached catalog and last-known-good model traffic-sample-v0 still work. Log and screenshot reading are local.', 'Retry now', 'Offline. Using cached data from 2026-09-13.'],
      stale: ['Position is 6 min old. Everything else is shown with its age.', 'How to update position', 'Position is stale.'],
      partial: ['Traffic covers 6 of 9 zones. Extract list not photographed, so extracts are catalog-possible only.', 'Choose extract manually', 'Some layers are incomplete.'],
      denied: ['Screenshot folder cannot be read. Manual map, extract and raid state still work.', 'Choose folder in Setup', 'Screenshot folder access denied.'],
      failed: ['Map drawing failed. The list view still has extracts, objectives and routes.', 'Retry map', 'Map failed to draw. List view available.']
    },
    intel: {
      empty: ['No search yet. Recent, pinned and planned items are listed.', 'Search items', 'Nothing searched yet.'],
      loading: ['Cached facts show now; prices refresh in the background.', 'Cancel refresh', 'Refreshing prices.'],
      offline: ['Cached catalog and prices from 2026-09-13 20:05. Prices are labelled with their age.', 'Retry now', 'Offline. Prices are from 2026-09-13.'],
      stale: ['Prices older than 1 hour are labelled stale and not used for SWAP advice.', 'Refresh prices', 'Prices are stale.'],
      partial: ['9 of 10 facts known. The missing barter source is named and not counted as zero.', 'Show what is missing', 'Some item facts are missing.'],
      denied: ['Price history needs the data folder, which cannot be written. Current facts still show.', 'Open Data settings', 'Data folder access denied.'],
      failed: ['Search index failed. Browse by category still works.', 'Rebuild search', 'Search failed. Browse still works.']
    },
    plan: {
      empty: ['No bundle yet. Three suggested bundles from your quests are ready to open.', 'Open a suggested bundle', 'No bundle yet.'],
      loading: ['Objectives list now; route estimate is calculating.', 'Skip route estimate', 'Calculating route.'],
      offline: ['Quests, hideout and last-known-good route model still work from cache.', 'Retry now', 'Offline. The plan uses cached data.'],
      stale: ['Profile progress was last imported 3 days ago and is labelled with that date.', 'Update progress', 'Quest progress is stale.'],
      partial: ['Food and water not checked; 2 of 3 requirements confirmed.', 'Check requirement', 'One requirement unknown.'],
      denied: ['Sharing to team needs the Member role. The plan stays private and editable.', 'Ask the owner', 'Sharing not allowed for your role.'],
      failed: ['Route model failed. Objectives, requirements and waypoints still work.', 'Retry route', 'Route failed. Waypoints still work.']
    },
    team: {
      empty: ['No team. Solo planning, marks on your own devices and pairing still work.', 'Create or join a team', 'Team: not in a team.'],
      loading: ['Last known members are shown with their age while connecting.', 'Cancel', 'Connecting to team relay.'],
      offline: ['Relay unreachable. Paired tablet still works on the local network. Marks queue locally.', 'Retry now', 'Team relay offline. Local pairing still works.'],
      stale: ['Moth has not updated for 2 min and is labelled stale, not removed.', 'Ask Moth to reconnect', 'A member is stale.'],
      partial: ['Birch shares position only. Loadout and quests are not shared by Birch.', 'What is shared', 'Some members share less.'],
      denied: ['Your role cannot clear other members’ marks. Your own marks still work.', 'Ask the owner', 'Not allowed for your role.'],
      failed: ['Invite could not be created. Existing team and devices unaffected.', 'Try again', 'Invite failed.']
    },
    debrief: {
      empty: ['No raids recorded yet. Import or finish a raid to see it here.', 'How raids are recorded', 'No raids recorded yet.'],
      loading: ['Raid list shows now; timeline details load when opened.', 'Cancel', 'Loading raid history.'],
      offline: ['All history is local and works offline. Only export to a team is unavailable.', 'Retry now', 'Offline. History is local.'],
      stale: ['This raid used model traffic-sample-v0; a newer model exists and is not applied retroactively.', 'Compare with newer model', 'A newer model exists.'],
      partial: ['Outcome not recorded for this raid. Unknown stays unknown.', 'Enter outcome', 'Raid outcome missing.'],
      denied: ['Export location cannot be written. History and corrections still work.', 'Choose another location', 'Export location denied.'],
      failed: ['Saving the correction failed. Your change is kept as a draft.', 'Retry save', 'Correction not saved. Draft kept.']
    },
    setup: {
      empty: ['First launch. Sample data can be explored before any setup.', 'Use sample data', 'Setup: nothing configured yet.'],
      loading: ['Checks run one at a time; finished checks show their result.', 'Stop checks', 'Running readiness checks.'],
      offline: ['Game data cannot sync. Everything local, including capture, still works.', 'Retry now', 'Offline. Local features still work.'],
      stale: ['Game data last synced 2 days ago. Next attempt at 19:00.', 'Sync now', 'Game data is stale.'],
      partial: ['4 of 6 readiness checks passed; 1 needs action and 1 is optional.', 'Go to next action', 'One setup item needs action.'],
      denied: ['The screenshot folder is not readable by the companion.', 'Choose folder', 'Screenshot folder access denied.'],
      failed: ['Text recognition self-test failed. Map, plan and team still work.', 'Show diagnostic', 'Text recognition unavailable.']
    }
  }
};
