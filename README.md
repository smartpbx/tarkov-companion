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

Text recognition prefers the recogniser built into Windows, which needs nothing installed. It
falls back to Tesseract, and *that* needs the
[Microsoft Visual C++ 2015-2022 x64 runtime](https://aka.ms/vs/17/release/vc_redist.x64.exe) —
so most people never need it, and Settings says which recogniser is actually running.
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
- **Only the exits you can take.** Running as a scav hides the PMC ones, because a door that
  will not open is worse than no marker at all.
- **What an exit asks of you.** Click it and the card says whether a switch has to be thrown
  first and what it costs, in money or by name.
- **The extracts this raid actually offered**, once you have photographed the list.
- **Names that arrange themselves.** Each one takes the first slot that covers neither another
  name nor a marker, and a name that fits nowhere is dropped rather than drawn over something.
- Quest objectives projected onto the map, with the quests that have something to do here
  listed beside it, pinned ones first.

### The raid

- Reads the game's own logs to follow the raid: loading, in raid, over. Scav or PMC. Survives
  the companion being started mid-raid.
- A summary when a raid ends, and a history of the ones before it.
- Your squad, read from the same logs.
- **Quests mark themselves.** The game announces every quest starting, failing and being handed
  in, and those are recorded as they happen. Nothing is inferred and nothing you recorded by
  hand is overwritten.

### Scanning

- Press the game's own screenshot key. That is the whole interface. The picture is read for
  items, extract lists, containers and flea listings, and the filename gives your position.
- Local text recognition, on your machine. No picture is uploaded and none is kept. It reads
  only the bright half of the picture, which is how this game draws every panel, because
  measured against real screenshots that turned a confident wrong answer into a right one.
- Screenshots older than a day go to the recycle bin, so the folder stops growing. The newest
  is always kept, only files the game named are touched, cloud placeholders are left alone, and
  nothing is deleted outright.
- It finds the game's screenshot and log folders wherever they are, OneDrive or not. Where it
  cannot, Settings takes the path and shows what it is watching.

### Knowing things

Item values and flea prices with local history, ammunition by what it actually penetrates, keys
and what they open, quest progress and what is available now, hideout requirements, current
events, and loadout analysis.

### Playing together

An opt-in group relay. Turn it on, type one group key, and your squad sees each other on one
map: position, heading, map, raid state, and their loadout and quests if they share them.

- Everyone has a colour of their own, the same on the map and in the list beside it, so three
  people on one map say which is which.
- **Waypoints** stay until somebody clears them and tick themselves off when you get there.
  **Pings** say "look here" and fade. Right-click to mark, hold shift to ping.
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
map, interior maps for buildings, a tablet companion so the game never has to be alt-tabbed,
serving the game data from the relay rather than every client syncing it, and reading health
and carried items out of a screenshot.

The game writes its own player's level nowhere, so that is typed on the Quests page. Its logs
carry a level on two thousand lines and every one belongs to somebody else.

## Licence

MIT. Map artwork comes from tarkov.dev under CC BY-NC-SA 4.0 and is fetched at runtime rather
than redistributed here. Third-party dependencies are inventoried in
[docs/THIRD_PARTY_INVENTORY.json](docs/THIRD_PARTY_INVENTORY.json), regenerated from the real
dependency graph and verified on every build.
