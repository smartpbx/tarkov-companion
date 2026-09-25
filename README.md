# Tarkov Companion

A second-screen companion for Escape from Tarkov. It watches the files the game already writes
and turns them into a live map, a raid record, and answers about items, ammunition, keys,
quests, the hideout and the flea market.

**External and read-only toward the game.** It does not read game memory, inject code, hook
rendering, inspect traffic, generate input, or draw over the game window. Everything it knows
comes from two ordinary folders: the game's logs, and the screenshots you take yourself.

That boundary is about not interfering with the game. [`docs/SAFETY.md`](docs/SAFETY.md) writes
it out, and `scripts/audit-safety.sh` enforces it on every build.

## Install

The V2 app ships as rough test builds. Once installed, it updates itself from the rough update
channel. It checks shortly after launch and every few hours. When a build is waiting, a notice
says so, and **Restart** installs it in place. Program and data live in separate folders, so an
update never touches your raids, profile or settings. The
[releases page](https://github.com/smartpbx/tarkov-companion/releases) carries the older builds.

The rough channel checks the SHA256 of every download before it installs anything. It does not
check who published the build; there is no signature yet. The installer is unsigned too, so
Windows SmartScreen warns the first time. The signed release ring that replaces it is
[#280](https://github.com/smartpbx/tarkov-companion/issues/280), and
[docs/RELEASES.md](docs/RELEASES.md#the-rough-channel) says exactly what the rough channel proves
and what it does not.

If a build misbehaves, **Setup › Updates & Diagnostics › Go back to the previous version** reinstalls the one
before it, checks its hash, and stays there until something newer is published.

Text recognition prefers the recogniser built into Windows, which needs nothing installed. It
falls back to Tesseract, and *that* needs the
[Microsoft Visual C++ 2015-2022 x64 runtime](https://aka.ms/vs/17/release/vc_redist.x64.exe).
Most people never need it, and Setup says which recogniser is actually running.

## Finding your way around

A rail on the left holds six workspaces:

- **Raid**: the map, your extracts, and the squad, during a raid.
- **Intel**: items, ammunition, keys, the flea market, crafts, your stash and Loot Scan results.
- **Plan**: quests, loadout, hideout and events, before a raid.
- **Team**: the squad, what you share with it, and paired tablets.
- **Debrief**: the raids you have played, and what they earned.
- **Setup**: folders, updates, appearance, language, data and privacy.

The V1 interface is still inside the build for anyone who needs it. Start the app with
`--ui-shell legacy`.

## What it does

### The map

- Every map from tarkov.dev, in photographic tiles or the hand-drawn plan where a map publishes
  both, chosen per map and remembered.
- **Your position and heading**, read from the filename of a screenshot you took. The game
  writes both into the name; nothing is captured and nothing is tracked.
- **Where you have been**, as a dotted trail. **Follow** moves the map to you when a new
  screenshot lands, at a zoom you choose.
- **The floor you are standing on**, chosen from your height, without you asking.
- **Four modes.** Navigate moves the map. Inspect says what is at the point you click: extracts,
  spawns, objectives and loot nearby, with distance. Route builds a plan stop by stop. Draw puts
  lines on the map, for yourself or for the squad.
- **Layers** for extracts, transits, spawns, locked doors, objectives, loot, marks and drawings.
  Each one remembers whether you left it on.
- **A loot layer with a value threshold**, so only spawns worth the walk are drawn. It shows
  known potential spawns from tarkov.dev, not what is in the raid.
- **Only your extracts.** The list follows your raid side, PMC or Scav, and hides the others,
  because a door that will not open is worse than no marker at all. Scav, PMC and shared differ
  in both colour and shape, because colour alone fails at twelve pixels and for a colourblind
  player.
- **What an exit asks of you.** Each extract lists its requirements. Where a switch has to be
  thrown first, it shows the chain in order, and what it costs.
- **Quest objectives**, as lettered pins, and a gold route through the ones on this map.
  **On it** in Plan puts a quest on the Raid map.

### The raid

- Reads the game's own logs to follow the raid: loading, in raid, over. Scav or PMC. Survives
  the companion being started mid-raid.
- A raid clock, from a screenshot of the extract list, from the raid start, or set by hand. It
  says which.
- **Quests mark themselves.** The game announces every quest starting, failing and being handed
  in, and those are recorded as they happen. Nothing is inferred and nothing you recorded by
  hand is overwritten.

### Scanning

- Press the game's own screenshot key. That is the whole interface. The picture is read for
  items, extract lists, containers, stash pages, quest lists and flea listings, and the filename
  gives your position. A frame it cannot place can be read again with **Read as…**.
- **Loot Scan.** Screenshot an opened container in raid, with your own grid visible. It names
  what it recognises and says take, swap or leave, with the reason. Items appear as they are
  read. Measured on real frames, the first scan of a session takes about a second and later ones
  a third of a second or less ([docs/PERFORMANCE.md](docs/PERFORMANCE.md)). Uncertain cells stay
  visible and are never promoted into a take.
- **Stash scan.** Scroll through your stash and screenshot each page. The pages are stitched into
  one stash, and owned counts feed Ammo, Keys and Loadout. Ammo and key cases get a guided scan of
  their own ([docs/STASH_SCAN.md](docs/STASH_SCAN.md)).
- **Quests from a TASKS screenshot.** An empty profile can start from screenshots of the game's
  quest list. Later screenshots offer a sync that applies nothing until you confirm it.
- Local text recognition, on your machine. No picture is uploaded, and none is kept unless you
  turn on Debug Capture. Each result says whether its image was kept.
- It finds the game's screenshot and log folders wherever they are, OneDrive or not. Where it
  cannot, Setup takes the path and shows what it is watching.
- Optional: screenshots older than the age you choose go to the recycle bin. Off until you turn
  it on in Setup › Game & Capture. Only files the game named are touched, and nothing is deleted
  outright.

Recognition is honest about its limits: item identity on real loot is still being measured and
improved ([#273](https://github.com/smartpbx/tarkov-companion/issues/273)), and the HEALTH screen
is not read yet. [docs/RECOGNITION.md](docs/RECOGNITION.md) and
[docs/LOOT_SCAN.md](docs/LOOT_SCAN.md) have the numbers.

### Knowing things

Item values and flea prices with local history, ammunition by what it actually penetrates, keys
and what they open, quest progress and what is available now, hideout requirements, crafts, and
loadout checks. Ammo, Keys and Loadout show what you own.

The flea market says **when its offers were seen**, gives its reasons in plain words, and works
out the flea fee from the catalog. The best sale names the flea or a trader.

Seasonal events, where a consumable is safe for some players and not others, are kept locally,
because no feed publishes them. You name an event, search for what it applies to, and mark each
item Safe or Allergic as you test it. Nothing about that is observed from the game.

The game writes its own player's level nowhere, so you type it on Plan. Its logs carry a level
on two thousand lines and every one belongs to somebody else.

### Debrief

Every raid you played, with its map, side, outcome, the extract used and what it earned. Filter
by game mode and wipe, so PvE and last wipe stay out of this wipe's numbers. Per-map coverage,
charts with a table beside each one, tags, notes, saved views, and archive and restore. A Loot
Scan that was wrong can be marked wrong.

### Playing together

An opt-in group relay. Turn it on, type one group key, and your squad sees each other on one
map: position, heading, map, raid state, and their loadout and quests if they share them.

- Everyone has a colour of their own, the same on the map and in the list beside it.
- **Marks.** Right-click to ping, a "look here" that fades. Hold Shift for a waypoint that stays
  until somebody clears it. Each mark has a colour, a scope (just me, or the squad) and a lifetime (a ping, 5 or 15 minutes, this raid,
  or until removed). Marks made while the relay is away are queued and sent when it comes back.
- **Ready check.** Each member sets Ready, and Team counts them. You can also share your chosen
  extract and a short note.
- **Squad quests.** Share your open objectives, by id, and Team, Raid and Plan show where the
  group's quests overlap.
- **Clock correction.** A PC clock that is minutes out would make every age wrong. The relay
  measures the difference and the companion corrects for it, and says so on Team.
- One reusable key is both which group you are in and proof you belong. The relay receives that
  key before hashing it for room storage, so transport hardening and scoped credentials remain
  open work in [#304](https://github.com/smartpbx/tarkov-companion/issues/304) and
  [#310](https://github.com/smartpbx/tarkov-companion/issues/310).
- While it is on, your companion also sends the kit, level, side and scav timer your game logged
  for the rest of your in-game party, and the relay passes them to everyone holding the room
  key. That is how a group shows you the kit your own game never tells you, and it is an open
  conflict with [`docs/SAFETY.md`](docs/SAFETY.md) owned by
  [#310](https://github.com/smartpbx/tarkov-companion/issues/310).
- Nothing is sent while it is off, and you stop publishing your position when your raid ends.
  Live positions are held in memory only. On disk the relay keeps the group's waypoints, the
  rooms its operator registered, problem reports as sent, and its updater's status.

### The tablet

Pair a tablet or phone from **Team › Devices**. It opens the relay's page in a browser and
follows the desktop's raid map: where everybody is, the group's marks, and the ability to place
one, with its colour, scope and lifetime, or draw a short route. Drag pans and pinch zooms.

Ask for **Control** and, once the desktop approves, the tablet switches the desktop's map and
keeps zoom and pan in step. It can arm the next capture. Stash scans and flea screens arrive on
it as review cards. The desktop can take control back at any time.

It is deliberately not a second copy of the application: the desktop stays complete on its own,
and somebody playing alone needs none of it. The protocol is
[docs/PAIRED_DEVICE_PROTOCOL.md](docs/PAIRED_DEVICE_PROTOCOL.md).

### Setup

- **Interface scale** up to 200% (Ctrl and + or −), and a separate **text size** up to 200% that
  leaves the window alone. Light, dark and high-contrast themes.
- **Language.** English today. Every word the app shows comes from a string table, so a
  translation needs no code change, and only languages with one are listed.
- **Feature flags.** Newer features, such as Draw and the tablet review cards, can be switched
  off. Setup lists each one and its default for your build.
- **Local only.** One switch and nothing leaves this PC. Or turn off one service at a time: game
  data, squad sharing, update checks, problem reports, TarkovTracker. Each says what it sends.
- **Report a problem** shows the whole report before it is sent, and sends nothing else. It
  leaves out logs, paths, screenshots, coordinates, names and credentials. **Copy diagnostics**
  shows the same text.

### Running the relay

One service on a small Linux host. An open relay needs no per-room setup, because the group key
picks the room. Keeping state across restarts needs a state directory, and the operator page
needs an admin key.

It updates itself every half hour from a **signed release ring**, not the public release. The
ring decision, manifest and archive are signature-verified before anything is unpacked. The new
build has to answer its health check as the signed version, and if it does not, the previous
build comes back. Until the host has the signing trust root and feed configured, the updater
refuses to run and the relay stays on the build it has.

Its operator gets a page at `/admin`, behind a key of its own that is not any group's key. It
says which build is running against which is published, registers the rooms that are meant to
exist, and lists the rooms in use that are not on that list. With nothing registered the relay
is open to anybody who can reach it; registering the first room closes it to every other one.

Problem reports reach the relay, and an hourly workflow opens an issue naming each one. The
relay holds no GitHub credential. Relay-side schema enforcement, retention and lifecycle for
those reports remain release-blocking work in
[#281](https://github.com/smartpbx/tarkov-companion/issues/281) and
[#310](https://github.com/smartpbx/tarkov-companion/issues/310).

Anything that can make an HTTPS request can join: the protocol is written out in full in
[docs/GROUP_RELAY.md](docs/GROUP_RELAY.md), and running one is
[deploy/group-server/README.md](deploy/group-server/README.md).

## Building it

Nothing here is built or tested on the maintainer's workstation; everything runs in GitHub
Actions. See [CONTRIBUTING.md](CONTRIBUTING.md) for the branch, worktree and verification rules,
and [docs/TESTING.md](docs/TESTING.md) for the tests.

A pull request merges on the Linux build and tests. **Windows verification** then runs on `main`:
it launches the packaged application on a real Windows runner, opens every page and photographs
it. A build that fails it is never published, and that gate exists because a build once passed
every test and could not open a window.

## What is not done, and what is next

Tracked as [issues](https://github.com/smartpbx/tarkov-companion/issues), and the shorter list
of things nobody has filed yet is [docs/BACKLOG.md](docs/BACKLOG.md). Still open from V2:
identity accuracy for real loot and stash items
([#273](https://github.com/smartpbx/tarkov-companion/issues/273)), magazine fit, which has no
data source yet ([#285](https://github.com/smartpbx/tarkov-companion/issues/285)), signed
releases ([#280](https://github.com/smartpbx/tarkov-companion/issues/280)), and the relay's
credentials and report lifecycle
([#304](https://github.com/smartpbx/tarkov-companion/issues/304),
[#310](https://github.com/smartpbx/tarkov-companion/issues/310)).

V2 built the engines. **V3** ([#712](https://github.com/smartpbx/tarkov-companion/issues/712)) is
the layer that decides what matters now. The app follows the phase from the log, the screenshot
and the clock, and changes its own surface, saying why, with an Undo. A small **Now** panel
replaces the long card stack on Raid: time left, where you are in words, where the squad is, what
to do next. Any screenshot gets an answer without arming anything first, and a loot verdict can
be spoken. The goal is close to zero clicks in the companion per raid, with the same boundary
toward the game. The concepts are in [docs/design/v3](docs/design/v3/README.md); none of it is in
the app yet.

![V3 concept: the Now panel in mid raid](docs/design/v3/v3-raid-now.png)

## Licence

MIT. Map artwork comes from tarkov.dev under CC BY-NC-SA 4.0 and is fetched at runtime rather
than redistributed here. Third-party dependencies are inventoried in
[docs/THIRD_PARTY_INVENTORY.json](docs/THIRD_PARTY_INVENTORY.json), regenerated from the real
dependency graph and verified on every build.
