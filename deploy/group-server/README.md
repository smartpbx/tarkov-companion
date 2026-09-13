# Deploying the group relay

The relay runs as a systemd service on a small container behind a Cloudflare tunnel. It holds
nothing on disk, so there is no data to migrate and no backup to take: members re-publish every
few seconds and a restart costs everybody one blink.

## First install

```
install -m 0755 tarkov-group-update.sh /opt/tarkov-group-update.sh
install -m 0644 tarkov-group-update.service /etc/systemd/system/
install -m 0644 tarkov-group-update.timer   /etc/systemd/system/
systemctl daemon-reload
systemctl enable --now tarkov-group-update.timer
systemctl start tarkov-group-update.service   # fetch the current build now
```

The service itself wants a unit that runs `/opt/tarkov-group/TarkovCompanion.GroupServer` with
`ASPNETCORE_URLS=http://0.0.0.0:8090`. It takes no configuration: since the group key became
the room, the server holds no secrets and there is nothing to set.

## Updating

It updates itself, every half hour, and that is the point. There is no interface to log into
and no button to press, so a relay that could not fetch its own builds would simply fall behind
the desktop client. That matters because the two speak a protocol: when the group key replaced
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
