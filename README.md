# Tarkov Companion

A second-screen companion for Escape from Tarkov. It watches the files the game already writes
and turns them into a live map, a raid record, and answers about items, ammunition, keys,
quests, the hideout and the flea market.

**External and read-only toward the game.** It does not read game memory, inject code, hook
rendering, inspect traffic, generate input, or draw over the game window. Everything it knows
comes from two ordinary folders: the game's logs, and the screenshots you take yourself.

That boundary is about not interfering with the game, and `scripts/audit-safety.sh` enforces it
on every build.

## Install

Download **`TarkovCompanionDesktop-win-Setup.exe`** from the
[latest build](https://github.com/smartpbx/tarkov-companion/releases/tag/dev) and run it once.
It installs per user, no administrator prompt, and starts itself.

After that it updates itself: it checks shortly after launch and every few hours, marks the
Settings entry in the sidebar when a build is waiting, and installs it in place when you say so.

Every published file carries a SHA256 in `VELOPACK-SHA256SUMS.txt`. Verifying before installing
is worth the ten seconds.

Local text recognition needs the
[Microsoft Visual C++ 2015-2022 x64 runtime](https://aka.ms/vs/17/release/vc_redist.x64.exe).
Everything else is self-contained.

## What it does

### The map

- Every map from tarkov.dev, in photographic tiles or the hand-drawn plan where a map publishes
  both, chosen per map and remembered.
- **Your position and heading**, read from the filename of a screenshot you took. The game
  writes both into the name; nothing is captured and nothing is tracked.
- **Where you have been**, as a dotted trail, and the view follows you when a new screenshot
  lands.
- **The floor you are standing on**, chosen from your height, without you asking.
- **Extracts, transits, spawns and locked doors**, drawn from the catalog. Scav, PMC and
  shared differ in both colour and shape, because colour alone fails at twelve pixels and for
  a colourblind player.
- **The extracts this raid actually offered**, once you have photographed the list.
- Quest objectives projected onto the map.

### The raid

- Reads the game's own logs to follow the raid: loading, in raid, over. Scav or PMC. Survives
  the companion being started mid-raid.
- A summary when a raid ends, and a history of the ones before it.
- Your squad, read from the same logs.

### Scanning

- Press the game's own screenshot key. That is the whole interface. The picture is read for
  items, extract lists, containers and flea listings, and the filename gives your position.
- Local text recognition, on your machine. No picture is uploaded and none is kept.
- Screenshots older than a day go to the recycle bin, so the folder stops growing. The newest
  is always kept, only files the game named are touched, and nothing is deleted outright.

### Knowing things

Item values and flea prices with local history, ammunition by what it actually penetrates, keys
and what they open, quest progress and what is available now, hideout requirements, current
events, and loadout analysis.

### Playing together

An opt-in group relay. Turn it on, type one group key, and your squad sees each other on one
map: position, heading, map, raid state, and their loadout and quests if they share them.

- **Waypoints** stay until somebody clears them. **Pings** say "look here" and fade.
- One key is both which group you are in and proof you belong. The server holds no secrets and
  never sees the key, only a hash of it.
- Nothing is sent while it is off. The relay keeps nothing on disk and forgets a member three
  minutes after they stop publishing.

Anything that can make an HTTPS request can join: the protocol is written out in full in
[docs/GROUP_RELAY.md](docs/GROUP_RELAY.md).

## Building it

Nothing here is built or tested on the maintainer's workstation; everything runs in GitHub
Actions. See [CONTRIBUTING.md](CONTRIBUTING.md) for the branch, worktree and verification rules.

The gate that matters is **Windows verification**: it launches the packaged application on a
real Windows runner, opens every page and photographs it. A build that fails it is never
published, and that gate exists because a build once passed every test and could not open a
window.

## What is not done

Tracked as [issues](https://github.com/smartpbx/tarkov-companion/issues). The larger ones: a 3D
map, interior maps for buildings, label collision on crowded maps, a tablet companion so the
game never has to be alt-tabbed, and an application icon.

## Licence

MIT. Map artwork comes from tarkov.dev under CC BY-NC-SA 4.0 and is fetched at runtime rather
than redistributed here. Third-party dependencies are inventoried in
[docs/THIRD_PARTY_INVENTORY.json](docs/THIRD_PARTY_INVENTORY.json), regenerated from the real
dependency graph and verified on every build.
