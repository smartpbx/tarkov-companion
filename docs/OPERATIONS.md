# Operations

Where things run, where they write, and what to do when one of them stops.

Everything here is about the running system. How it is built is `TESTING.md`; how the Windows
package is proven is `WINDOWS_VERIFICATION.md`.

## The desktop application

**Where it installs.** Velopack, per-user, under `%LOCALAPPDATA%\TarkovCompanionDesktop`.
Nothing needs administrator rights and nothing is written to Program Files.

**Where it keeps things.**

| What | Where |
| --- | --- |
| Database | `%LOCALAPPDATA%\TarkovCompanion\tarkov-companion.db` |
| Settings | `%LOCALAPPDATA%\TarkovCompanion\Config` |
| Cached map artwork | `%LOCALAPPDATA%\TarkovCompanion\Cache\Maps` |
| Logs | `%LOCALAPPDATA%\TarkovCompanion\Logs` |

A portable install (`portable.flag` beside the executable) puts all of them under `Data\`
next to the application instead.

**Where it does not keep things.** It never writes inside the game's folders except to move
old screenshots to the recycle bin, and only when that is switched on.

**How it updates.** It checks the `dev` release on every launch and installs what it finds,
provided the checksum matches what was published. Only builds that passed Windows verification
are ever published there, so the shortcut does not skip the checks.

## The group relay

| | |
| --- | --- |
| Host | Proxmox CT 115 |
| Address | `10.10.10.80:8090` on the LAN, and a Cloudflare hostname in front of it |
| Unit | `tarkov-group.service` |
| Update timer | `tarkov-group-update.timer`, every 30 minutes |
| State | `/var/lib/tarkov-group` once `StateDirectory=tarkov-group` is set on the unit |

**What it stores.** The squad's waypoints, and nothing else. Positions are held in memory for
three minutes and never written down; pings expire in forty-five seconds and are not persisted,
because one restored from disk would be claiming "now".

**How it updates itself.** The timer fetches the published archive, verifies its checksum
against what the release says, swaps `/opt/tarkov-group`, and rolls back if the new build does
not answer `/health`. The archive is packed reproducibly, so a build whose server did not
change produces the same checksum and no restart happens.

## Problem reports

A player presses **Report a problem** on Settings. The report goes to the relay, which keeps it
in `/var/lib/tarkov-group/reports` and hands back a reference. The hourly `relay-watch.yml`
lists what is waiting and opens one issue per reference.

**The relay holds no GitHub credential.** The workflow files the issues with the token GitHub
Actions already gives it for its own repository, so the internet-facing box never holds a
long-lived token with write access to anything.

**The issue names a reference; the body stays on the relay.** This repository is public, and a
report describes somebody's machine. To read one:

```bash
curl -H "X-Admin-Key: $TARKOV_RELAY_ADMIN_KEY" https://<relay>/reports/<reference>
```

The admin key is in two places and nowhere else: the repository secret
`TARKOV_RELAY_ADMIN_KEY`, and `/etc/systemd/system/tarkov-group.service.d/10-reports.conf` on
CT 115. It is not the group key — any member of any group holds one of those, and this lists
every group's reports.

## The relay panel

`https://<relay>/admin`, with the same admin key typed into the page. It shows which build is
running and how long it has been up, which rooms are registered, and — the row that matters —
which rooms currently have members and are **not** registered.

By default the relay serves any room anybody's key hashes to. Registering the first room closes
it to every other one, so the order is: open the panel, adopt each room that has your friends in
it, then check that the unregistered list is empty. See `docs/GROUP_RELAY.md` for what the three
ways of registering a room mean.

The list is `rooms.json` in the relay's state directory (`/var/lib/tarkov-group`), which is
outside the tree the updater replaces, so it survives the half-hourly update. Deleting that file
reopens the relay; it never locks anybody out permanently.

The page also says which build is running against which is published, including the case worth
catching: a build that installed, failed its health check and was rolled back is recorded in
`REFUSED_SHA256` and not retried until a newer one is published, so a relay stuck behind for
that reason used to look exactly like one that was up to date.

**Update now** writes `UPDATE_NOW` in the state directory. `tarkov-group-update.path` watches
for it and runs the same update the timer runs — the relay runs unprivileged and cannot start a
unit itself. If that path unit is not installed the button still works, in the sense that the
next timer tick picks the file up; it is just no longer immediate.

A report intentionally excludes game logs, the group key, and screenshot pixels. It includes
diagnostic detail, an application-log tail, and the *shape* of recent screenshot names with every
digit masked. Complete path, filename, and coordinate filtering plus an outbound preview remain
release-blocking work in #281 and #310.

**When the group panel says something is wrong:**

| What it says | What it means |
| --- | --- |
| "Needs an https address…" | the key travels on every request; only a local address may use http |
| "The group key must be between 8 and 128 characters" | a 401, which is a length check — not a wrong key |
| "…last heard 42s ago" | the relay stopped answering; the last good picture is still on screen |
| "no position: the game's screenshot folder has not been found" | this companion is in the room and has nothing to put on the map |

A key that is merely *different* is not an error. The key **is** the room, so a typo puts
somebody in a room of their own where everything works and nobody is there.

## When `dev` is missing or stale

The install link, the in-app updater and the relay updater all read the same rolling
pre-release. If it is gone or behind:

1. Check the latest run of `windows-verify.yml` on `main`, and its **`publish` job** in particular. Publishing is its own job: it needs `windows-verify` to have passed, it is the only job in the workflow holding a write token, and it does not run for a pull request at all.
2. The publish never deletes the release, uploads packages before the feed files, and refuses
   to publish a version below what is already live — so a half-finished run leaves the previous
   build whole rather than leaving the feed pointing at nothing.
3. Re-running the failed job republishes; there is nothing to clean up by hand.

`update.json` on the release is the authority on what is actually published: it carries the
version, the commit, the build time and the checksum.

## The fast loop

CT 114 on Proxmox, `/root/repos/tarkov-companion`. The .NET 10 SDK is at `/root/.dotnet` and is
not on `PATH`. A build is about fifteen seconds, the whole suite about twenty.

Its `/root/repos` is a symlink to a path that *looks* like the workstation's
(`/home/cmannerow/Nextcloud/Documents/programming`) and is the container's own disk. Build
output there prints workstation-shaped paths and is not the workstation.
