# Deploying the group relay

The relay runs as a systemd service on a small container behind a Cloudflare tunnel. Live member
positions and pings are memory-only, but a configured state directory persists registered rooms,
waypoints, problem-report bodies, and updater control files. Back up only the selected durable
payload described in `docs/runbooks/relay-operations.md`; updater stamps and requests must not be
restored from backup.

## First install

```
install -m 0755 tarkov-group-update.sh /opt/tarkov-group-update.sh
install -m 0644 tarkov-group-update.service /etc/systemd/system/
install -m 0644 tarkov-group-update.timer   /etc/systemd/system/
install -m 0644 tarkov-group-update.path    /etc/systemd/system/
systemctl daemon-reload
systemctl enable --now tarkov-group-update.timer
systemctl enable --now tarkov-group-update.path
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
`ASPNETCORE_URLS=http://0.0.0.0:8090`. Group traffic needs no server-side copy of a group key,
but operator routes require the separately protected `TARKOV_RELAY_ADMIN_KEY` drop-in described
in `docs/OPERATIONS.md`.

## Updating

It updates itself, every half hour, and that is the point: a relay that could not fetch its own
builds would simply fall behind the desktop client. That matters because the two speak a protocol: when the group key replaced
a room and a server-side secret, a client that had updated could not talk to a server that had
not.

The update is arranged so a failure leaves the service on the build it was already running:

- the checksum is verified against the published one **before** anything is unpacked
- a rollback copy is taken **before** anything is replaced
- the new build has to **answer** `/health`, not merely start, because a process that starts
  and then fails to serve is exactly the failure worth catching and systemd calls it success
- if it does not answer, the previous build goes back and the run reports failure

A stamp file records the checksum in place, so a timer that fires every half hour does nothing
at all unless the published build actually changed. Without it every member would disappear and
reappear twice an hour for no reason.

A build that installed, failed its health check and was rolled back is written to
`REFUSED_SHA256` and not retried until a newer one is published. That is the right behaviour and
it used to be invisible: a relay stuck behind for that reason looked exactly like one that was
up to date. The panel reads both files and says which it is.

## Why wget and not curl

The container has no `curl` and is awkward to give one. `wget` ships on a minimal Debian, so
the updater uses that. This is written down because the first version of the script used curl,
failed instantly on the real container, and the runbook had already recorded that exact fact.

## Watching it

```
systemctl list-timers tarkov-group-update.timer
journalctl -u tarkov-group-update.service -n 50
wget -qO- https://tarkov.mannerow.net/health
```

## Keeping relay state across an update

The relay holds durable state in memory unless it is told where to put it, and the updater
replaces `/opt/tarkov-group` wholesale—so anything written inside the tree would be destroyed by
the update it is meant to survive.

Add to `/etc/systemd/system/tarkov-group.service`:

```ini
[Service]
StateDirectory=tarkov-group
```

systemd then creates `/var/lib/tarkov-group`, owns it correctly whether or not the unit uses
`DynamicUser=`, and passes the path in `STATE_DIRECTORY`. The server writes `marks.json`,
`rooms.json`, and `reports/` there; the updater writes its install/refusal stamps and immediate
update request in the same directory. For a deployment that is not systemd, set
`TARKOV_GROUP_STATE` to a writable directory instead.

**No live movement state.** Pings expire in forty-five seconds and mean "now", so one restored
from disk would be a lie. Positions are never written at all. Registered rooms, waypoints, and
submitted report bodies survive deliberately; report bodies are restricted diagnostic material.

Until `StateDirectory=` is set on the running unit, the server behaves exactly as it did
before: marks live in memory and a restart clears them.
