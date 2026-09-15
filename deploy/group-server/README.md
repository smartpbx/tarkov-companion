# Deploying the group relay

The relay runs as a systemd service on a small container behind a Cloudflare tunnel. Live member
positions are memory-only and members re-publish every few seconds. Its state directory does
persist waypoints, the room registry, problem reports, and updater status, so an operator must
include that directory in migration, retention, and backup decisions.

## First install

```
install -m 0755 tarkov-group-update.sh /opt/tarkov-group-update.sh
install -m 0644 tarkov-group-update.service /etc/systemd/system/
install -m 0644 tarkov-group-update.timer   /etc/systemd/system/
install -m 0644 tarkov-group-update.path    /etc/systemd/system/
systemctl daemon-reload
systemctl enable --now tarkov-group-update.timer
systemctl enable --now tarkov-group-update.path
# configure the feed, ring, token and trust root first: docs/RELEASES.md#the-relay-updater
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
host has the feed, ring, token and Sigstore trust root described in
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

## Why wget and not curl

The container has no `curl` and is awkward to give one. `wget` ships on a minimal Debian, so
the updater uses that. This is written down because the first version of the script used curl,
failed instantly on the real container, and the runbook had already recorded that exact fact.

## Watching it

```
systemctl list-timers tarkov-group-update.timer
journalctl -u tarkov-group-update.service -n 50
cat /var/lib/tarkov-group/INSTALLED_VERSION /var/lib/tarkov-group/PUBLISHED_VERSION
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
- `rooms.json`: registered room hashes and their labels
- `reports/*.md`: problem reports exactly as sent, kept until an operator deletes them
- `UPDATE_NOW`, beside the updater's own `INSTALLED_SHA256` and `REFUSED_SHA256`

Pings are not written: they expire in forty-five seconds and mean "now", so one restored from
disk would be a lie. Live member positions and trails are not written either. A report body can
still contain folder paths and screenshot coordinates, so back up and delete `reports/` as
sensitive data.

Until `StateDirectory=` is set on the running unit, marks and the room registry live in memory
and a restart clears them, reports are not kept, and the panel cannot ask for an update.
