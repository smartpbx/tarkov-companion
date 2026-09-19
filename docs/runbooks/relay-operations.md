# Relay and diagnostics operations runbook

Use UTC in incident notes. Never put group/admin/diagnostic keys, screenshots, OCR crops, names, coordinates, report bodies, raw logs, or full paths in a ticket.

## Readiness alert

1. Use the single relay-watch issue; record its sanitized reason and UTC time.
2. Check public `/health` for its liveness and build (`status`, `protocol`, `version`, `commit`; nothing else).
3. With the operator key, `GET /admin/readiness` (header `X-Admin-Key`) says which check failed (`storage`, `disk`, `clock`, `build`, `updaterCheck`, `latency`, `failures`) and whether rate-limit or rejected-input pressure is elevated. Confirm what it says against the service journal, `df`, the clock and the updater's status directory before acting on it.
4. Correct the failed condition, wait one scheduled watch interval, and comment on the existing incident. Do not open a duplicate.

## Backup and restore

Back up only the durable payload that should survive a host loss: `rooms.json`, `marks.json`, and, when explicitly required, the complete `reports/` directory. A report backup contains submitted bodies, not merely references, and is restricted diagnostic material. Live positions and pings are memory-only. Group, admin, and diagnostic keys do not belong in this backup.

Exclude `UPDATE_NOW` from the relay-state backup. It is a transient request, and restoring it can trigger an unintended deployment. Authenticated update history and install/refusal records live separately under root-owned `/var/lib/tarkov-group-update`; preserve that directory only as one access-controlled host-state backup, never as selected scalar stamps. `/var/lib/tarkov-group-update-status` contains derived panel copies and is not authority. A replacement host without the authoritative history must use the provisioned floor or verified bootstrap procedure in `docs/RELEASES.md`, not an old status mirror.

1. Stop `tarkov-group.service` in a maintenance window.
2. Create an encrypted, access-controlled copy of the selected payload outside `/opt/tarkov-group`; record only backup id, UTC, checksum, and operator.
3. Start the service and verify public liveness; authenticated readiness is not wired yet.
4. To restore, stop the service, preserve the failed payload under the same controls, and restore only the selected durable files atomically. Keep the current host's authenticated updater history; on a replacement host follow the signed-feed bootstrap procedure. Ensure `UPDATE_NOW` is absent, start the service, and verify a test group.

Desktop diagnostic state is backed up/restored only through #270's recovery interface. Never invent a diagnostic SQLite table.

## Key rotation

The current developer command channel accepts one literal `TARKOV_COMPANION_DIAGNOSTIC_TOKEN`; it does not consume `DiagnosticTokenSet`. Do not configure `new,old@<UTC expiry>` today: it would become the literal required token and break both token values.

After #294 wires the token-set consumer, the rotation procedure will be: generate a new token of at least 32 random characters in the secret manager, configure one current and bounded-expiry previous value, verify fixture requests before/after expiry, then revoke the old value. Until then, replace the single token in one maintenance operation and verify only the new value. Never print either value in a terminal transcript.

The relay admin-key reader accepts only one value, so rotate it in a short maintenance window: generate a new random value in the secret manager; update the GitHub Actions secret and the protected systemd drop-in without printing either value; run `systemctl daemon-reload` and restart the relay; verify the admin route and one report-list request with the new value; then prove the old value receives 401. A group key is a membership secret, not an operator key.

## Update, maintenance, and disaster recovery

For an old build, distinguish the failure before changing state. Relay watch proves only that the service is live on a known default-branch commit; it does not decide what a signed ring selected or whether an older build is intentional. Inspect the admin panel, the root-owned `INSTALLED_RELEASE.json`, `PUBLISHED_RELEASE.json`, `REFUSED_RELEASE.json`, updater journal, configured ring, pause/rollback decision, and trust-root/feed errors. Follow `docs/RELEASES.md`; never substitute the retired public `dev` release or a checksum downloaded beside an archive for signed evidence. A failed health check must roll back, and a partly unpacked build must never be forced live. Resolve disk pressure only after a verified backup; repair clock synchronization before retrying expiry-sensitive operations.

For host loss, provision from the released package, restore the approved restricted state backup, configure the protected admin-key drop-in, and verify liveness before switching address. An absent backup means an honest loss of waypoints/registrations; never reconstruct positions or pings.

## Privacy or diagnostic incident

1. Stop the affected report/export path. `TARKOV_COMPANION_INTERNAL_TELEMETRY` is currently only an unwired request flag; do not claim toggling it disabled a collector that does not exist.
2. Revoke implicated diagnostic/admin tokens and preserve minimum sanitized metadata.
3. Determine whether a prohibited field left the device/relay without reproducing it with real evidence.
4. Delete affected diagnostic material only through its actual reviewed storage operation when retention obligations permit, and report the outcome. The closed event model has no durable store yet.
5. Add a complete-bundle, transport, and persisted-body redaction test before re-enabling the affected path.

## Problem-report pickup

The Actions workflow validates the whole bounded report listing before it writes any issue and never downloads report bodies. The live relay currently lists each report under its timestamp-prefixed stored filename, while `GET /reports/{reference}` accepts the original 12-hex reference. This mismatch is owned by #310. Until it is aligned, the workflow must fail closed with no report issues; do not strip the timestamp in monitoring code or describe an issue reference as retrievable.
