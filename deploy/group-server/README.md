# Deploying the group relay

The relay runs as a systemd service on a small container behind a Cloudflare tunnel. Live member
positions are memory-only and members re-publish every few seconds. Its state directory does
persist waypoints, the room registry, problem reports, and the panel's update request, and the
updater keeps its install record in two root-owned directories of its own, so an operator must
include all three in migration, retention, and backup decisions.

## First install

```
install -m 0755 tarkov-group-update.sh /opt/tarkov-group-update.sh
install -m 0644 tarkov-group-update.service /etc/systemd/system/
install -m 0644 tarkov-group-update.timer   /etc/systemd/system/
install -m 0644 tarkov-group-update.path    /etc/systemd/system/
systemctl daemon-reload
systemctl enable --now tarkov-group-update.timer
systemctl enable --now tarkov-group-update.path
# first: install a pinned cosign, and configure the feed, ring, token, trust root and floor (docs/RELEASES.md#the-relay-updater)
systemctl start tarkov-group-update.service   # fetch the current build now
```

The `.path` unit is what makes the panel's **Update now** button do anything. The relay runs
unprivileged and cannot start a unit; it writes `UPDATE_NOW` in its state directory, and this
watches for that file and runs the same update the timer runs. Without it the button writes a
file nothing reads, and the update still happens — at the next tick, up to half an hour later.

**This is the last hand install.** The updater now ships these three units and itself inside the
archive, and installs them after a new build has answered `/health`. Before that it replaced the
server tree and nothing else, so every fix to this directory sat in the repo doing nothing: the
relay was running the 13 September updater a day after its stamp bug was fixed here, with the
bug live. `tarkov-group.service` is still yours — it is hand-maintained, carries the admin key
drop-in, and is the one unit this repository does not know the contents of.

The service itself wants a unit that runs `/opt/tarkov-group/TarkovCompanion.GroupServer` with
`ASPNETCORE_URLS=http://0.0.0.0:8090`. Open mode needs no per-room configuration: each request
supplies the reusable group key, which the relay receives before hashing it into a room id. The
operator/admin credential and optional registered-room state remain separate configuration.

## Updating

It updates itself, every half hour, and that is the point: a relay that could not fetch its own
builds would simply fall behind the desktop client. That matters because the two speak a protocol: when the group key replaced
a room and a server-side secret, a client that had updated could not talk to a server that had
not.

It follows a **signed release ring** in a private feed, not the public `dev` release. Until the
host has the pinned cosign, feed, ring, token, Sigstore trust root and floor described in
[`docs/RELEASES.md`](../../docs/RELEASES.md#the-relay-updater), the updater refuses to run and the
relay stays on the build it has. That page also has the steps for moving an existing relay over.

The update is arranged so a failure leaves the service on the build it was already running:

- the ring decision, the manifest and the archive are each **signature-verified** against the
  trust root on the host, and against the one workflow allowed to publish, **before** anything is
  unpacked; a checksum from the same place as the archive proves nothing about who put it there
- an older build is refused unless the ring's signed decision is a rollback, and an older ring
  decision is refused outright
- a rollback copy is taken, and a journal of the units, the updater and the stamps is written,
  **before** anything is replaced
- the new build has to **answer** `/health` as the signed version, commit and protocol, not merely
  start, because a process that starts and then fails to serve is exactly the failure worth
  catching and systemd calls it success
- if it does not, or anything after the swap fails, the previous build, units, updater and stamps
  all go back and the run reports failure; a run that was killed is undone by the next one

The stamps record what is installed only after it has proved itself, so a timer that fires every
half hour does nothing unless the ring actually moved, and never claims a build that was rolled
back. Without that, every member would disappear and reappear twice an hour for no reason, and a
refused build would look installed.

A build that installed, failed its check and was rolled back is written to `REFUSED_SHA256` and
`REFUSED_RELEASE.json` and not retried until the ring publishes a new signed decision. That is the
right behaviour and it used to be invisible: a relay stuck behind for that reason looked exactly
like one that was up to date. The panel reads the stamps and says which it is.

A paused ring holds the relay where it is; a signed rollback is applied even while paused.

### Where the updater keeps what it decides from

The relay runs as an unprivileged dynamic user that owns `/var/lib/tarkov-group`. The updater runs
as root. So nothing root decides from lives in the relay's directory: a journal the relay could
write would be a journal it could fill with an "updater" for root to install. Three directories,
three owners:

| Directory | Owner and mode | Holds | Read by |
| --- | --- | --- | --- |
| `/var/lib/tarkov-group-update` | root, `0700` (the update unit's `StateDirectory=`) | `update.lock`; `work.*` download and verification directories; the `swap` journal (assembled as `swap.new`, committed by renaming to `swap.committed`); authoritative `INSTALLED_RELEASE.json` and `PUBLISHED_RELEASE.json`; their `INSTALLED_SHA256`, `INSTALLED_VERSION`, `INSTALLED_COMMIT`, `INSTALLED_RING`, `INSTALLED_GENERATION`, `PUBLISHED_SHA256`, `PUBLISHED_VERSION`, `PUBLISHED_RING`, `PUBLISHED_GENERATION`, `PUBLISHED_MANIFEST_SHA256` mirrors; `REFUSED_SHA256`, `REFUSED_RELEASE.json` | the updater only |
| `/var/lib/tarkov-group-update-status` | root, `0755`, files `0644` | copies of `INSTALLED_SHA256`, `INSTALLED_VERSION`, `PUBLISHED_SHA256`, `PUBLISHED_VERSION` and `REFUSED_SHA256` | the relay's panel; never read back by the updater |
| `/var/lib/tarkov-group` | the relay's dynamic user | the relay's own state, below, and `UPDATE_NOW` | the updater unlinks `UPDATE_NOW` and touches nothing else |

The updater refuses to run if its state directory is a link, belongs to anyone else, or cannot be
made `0700`. Each release record is committed by one rename before its scalar mirrors, so a killed
run cannot combine a new generation with an old decision digest; the next run repairs any mirrors
it interrupted. `INSTALLED_SHA256` and `REFUSED_SHA256` files that the checksum updater left in
`/var/lib/tarkov-group` are no longer read or written by anything and can be deleted.
Its default install, rollback and updater paths must stay below `/opt`, all state trees below
`/var/lib`, and units in `/etc/systemd/system`. A custom layout must set
`TARKOV_UPDATE_PATH_ROOT` to a deeper common ancestor and put every mutable tree, the updater and
unit directory below it. Those managed destinations must be canonical, root/updater-owned and
not group/world-writable. Run `tarkov-group-update.sh --validate-paths` for a non-mutating
containment, ownership and overlap check before enabling the service.

## The rough update channel

The relay also serves the desktop's rough test builds, as plain files, at
`/updates/rough/`. What that channel proves and does not is in
[`docs/RELEASES.md`](../../docs/RELEASES.md#the-rough-channel). This is the operator's half.

It is apart from everything else the relay does. `GET` and `HEAD` only, no key, no state, no
upload route, nothing read at startup and nothing running when nobody is downloading. It serves
`/srv/tarkov-updates/<channel>/<file>` and refuses any name that is not a plain file name. The
folder is outside `/opt/tarkov-group`, so a relay update does not remove it, and outside the
relay's state directory, so the relay itself cannot write to it. `tarkov-group.service` needs no
change: `ProtectSystem=strict` still allows reading `/srv`. Set `TARKOV_UPDATE_FEED_ROOT` in a
drop-in only if the folder has to be somewhere else.

**Once:** the relay has to be running a build that has this route. Deploy it the way you deploy
any relay build, then make the folder:

```bash
ssh proxmox 'pct exec 115 -- install -d -m 0755 /srv/tarkov-updates/rough'
```

**Each build.** Take the run id of a green Windows verification run on `main`; its version is
`2.0.<run number>`. From a machine with `gh`, this repository and SSH to the Proxmox host:

```bash
RUN=<run id>
rm -rf /tmp/rough-channel
gh run download "$RUN" --repo smartpbx/tarkov-companion --name rough-channel --dir /tmp/rough-channel
python3 scripts/release/rough_channel.py verify /tmp/rough-channel    # must print "ok ..." and exit 0
scp -r /tmp/rough-channel proxmox:/tmp/rough-channel
ssh proxmox '
  set -e
  while read -r name; do
    pct push 115 "/tmp/rough-channel/$name" "/srv/tarkov-updates/rough/$name" --perms 0644
  done < /tmp/rough-channel/COPY-ORDER.txt
  keep=$(grep -e "-full.nupkg$" /tmp/rough-channel/COPY-ORDER.txt)
  pct exec 115 -- find /srv/tarkov-updates/rough -name "*.nupkg" ! -name "$keep" -delete
  rm -rf /tmp/rough-channel'
```

What that copies, in this order, is exactly the three files in `COPY-ORDER.txt`:

1. `TarkovCompanionDesktop-<version>-full.nupkg`, the package an installed build downloads
2. `TarkovCompanionDesktop-win-Setup.exe`, the installer somebody runs once
3. `releases.win.json`, the feed, **last**: a client must never read a feed naming a package that
   has not arrived

Files must be world-readable (`0644`, folder `0755`) because the relay runs as a dynamic user.
The `find` afterwards removes older packages: each build is about 190 MB and CT 115 has an 8 GB
disk. Then check it from outside:

```bash
curl -s https://tarkov.mannerow.net/updates/rough/releases.win.json      # names the new version
curl -sI https://tarkov.mannerow.net/updates/rough/TarkovCompanionDesktop-win-Setup.exe | head -1
```

To take a build back, publish an older run's folder the same way **and** tell the testers: an
installed client never moves to a lower version by itself, so a withdrawn build stays on the
machines that already took it until a newer one is published.

## Why wget and not curl

The container has no `curl` and is awkward to give one. `wget` ships on a minimal Debian, so
the updater uses that. This is written down because the first version of the script used curl,
failed instantly on the real container, and the runbook had already recorded that exact fact.

## Watching it

```
systemctl list-timers tarkov-group-update.timer
journalctl -u tarkov-group-update.service -n 50
cat /var/lib/tarkov-group-update-status/INSTALLED_VERSION /var/lib/tarkov-group-update-status/PUBLISHED_VERSION
wget -qO- https://tarkov.mannerow.net/health
```

## Keeping state across an update

The relay holds its state in memory unless it is told where to put it, and the updater
replaces `/opt/tarkov-group` wholesale — so anything written inside the tree would be
destroyed by the update it is meant to survive.

Add to `/etc/systemd/system/tarkov-group.service`:

```ini
[Service]
StateDirectory=tarkov-group
```

systemd then creates `/var/lib/tarkov-group`, owns it correctly whether or not the unit uses
`DynamicUser=`, and passes the path in `STATE_DIRECTORY`. For a deployment that is not systemd,
set `TARKOV_GROUP_STATE` to a writable directory instead. The server writes:

- `marks.json`: waypoints, including who placed and who reached each one
- `rooms.json`: registered room hashes and their labels. **If this file exists and cannot be read,
  the relay serves no room at all** and every group path answers 503 saying so, rather than
  falling back to the open behaviour an empty file means (#317). Restore it, delete it, or
  register a room from the panel — which rewrites it — and the relay serves again. The panel's own
  access line says which of the three states it is in.
- `relay-devices.json`: the paired-device registry (owner and paired devices' key thumbprints,
  sessions, audit trail); no private keys and no plaintext bearer credential
- `relay-desktops/`: one file of the same shape (and its `.backup`) per desktop that registered
  itself (#553), plus `index.json` naming each desktop's room. `relay-devices.json` stays as it
  was: it is the desktop that claimed the relay before #553, and needs no migration step
- `reports/*.md`: problem reports exactly as sent, kept until an operator deletes them
- `UPDATE_NOW`, the panel's request for an update; the updater's stamps are not here but in its
  own directories, [above](#where-the-updater-keeps-what-it-decides-from)

Pings are not written: they expire in forty-five seconds and mean "now", so one restored from
disk would be a lie. Live member positions and trails are not written either. The ordinary
desktop report is closed and excludes paths and coordinates, but this endpoint still accepts
arbitrary caller bodies, so back up and delete `reports/` as sensitive data.

Until `StateDirectory=` is set on the running unit, marks and the room registry live in memory
and a restart clears them, reports are not kept, and the panel cannot ask for an update.

### Upgrading onto a build with the device registry (#289, #290, #407)

`relay-devices.json`'s shape (owner and paired devices' key thumbprints, sessions, audit trail —
see above) has not changed since it was introduced (#278, 2026-09-16/17); today's three PRs
(#506, #514, #516) add new routes that read and write the *same* records, not a new file or a new
field. So upgrading from a relay that already runs #278 or later carries the owner and every
paired tablet forward with nothing to redo.

Upgrading from an older build — one that never wrote this file at all — is different. On first
start, `VerifiedRelayRegistryStore.LoadAsync` finds neither `relay-devices.json` nor
`relay-devices.json.backup` and returns an empty, uninitialized registry rather than an error; the
process starts normally, but every device-scoped route (pairing, resume, revoke, map, frames)
refuses until something claims the relay. **The owner must claim once more with the admin key**
(`POST /admin/relay/claim`, `X-Admin-Key`/`TARKOV_RELAY_ADMIN_KEY`) after such an upgrade — the new
key-possession resume route has no prior owner key to resume against. That one claim writes the
desktop's key to `relay-devices.json` as owner; every later restart resumes by key possession alone
(no admin key) for as long as the state directory survives. The same empty-registry path is what
runs if `relay-devices.json` is corrupt or its backup disagrees with it (#317): the registry closes
to the admin-key/owner-recovery ceremony rather than guessing, so a wiped or unreadable state
directory also costs the owner one more admin-key claim, and every paired tablet one more pairing.

No new environment variable. New routes added by #506/#514/#516, all under
`/v2/companion/relay/`: `POST devices/{deviceId}/revoke`, `POST possession/challenge`,
`POST owner/resume`, `POST resume/requests`, `GET resume/requests/{ticketId}`,
`POST resume/requests/{ticketId}/offer`, `POST frames/reset`. Deploy them the way every other
route deploys — there is nothing route-specific to configure.
