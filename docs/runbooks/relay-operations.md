# Relay and diagnostics operations runbook

Use UTC in incident notes. Never put group/admin/diagnostic keys, screenshots, OCR crops, names, coordinates, report bodies, raw logs, or full paths in a ticket.

## Readiness alert

1. Use the single relay-watch issue; record its sanitized reason and UTC time.
2. Check public `/health` for its liveness/build indication. It currently also exposes aggregate room/member counts, so do not treat it as a minimal liveness-only surface.
3. There is currently no readiness route. Inspect the service journal, disk, clock, and updater state directly; do not claim a `RelayReadiness` result was observed.
4. Correct the failed condition, wait one scheduled watch interval, and comment on the existing incident. Do not open a duplicate.

## Backup and restore

The relay backup may include registered rooms, waypoints, and the complete bodies of stored reports because it copies the state directory. It must exclude live positions, pings, group keys, admin keys, and diagnostic tokens. Treat a backup containing `reports/` as restricted diagnostic material, not as a list of report references.

1. Stop `tarkov-group.service` in a maintenance window.
2. Create an encrypted, access-controlled state-directory copy outside `/opt/tarkov-group`; record only backup id, UTC, checksum, and operator.
3. Start the service and verify public liveness; authenticated readiness is not wired yet.
4. To restore, stop the service, preserve the failed state directory under the same controls, restore atomically, start, and verify a test group.

Desktop diagnostic state is backed up/restored only through #270's recovery interface. Never invent a diagnostic SQLite table.

## Key rotation

The current developer command channel accepts one literal `TARKOV_COMPANION_DIAGNOSTIC_TOKEN`; it does not consume `DiagnosticTokenSet`. Do not configure `new,old@<UTC expiry>` today: it would become the literal required token and break both token values.

After #294 wires the token-set consumer, the rotation procedure will be: generate a new token of at least 32 random characters in the secret manager, configure one current and bounded-expiry previous value, verify fixture requests before/after expiry, then revoke the old value. Until then, replace the single token in one maintenance operation and verify only the new value. Never print either value in a terminal transcript.

Rotate the relay admin key separately. A group key is a membership secret, not an operator key.

## Update, maintenance, and disaster recovery

For an old build, inspect `REFUSED_SHA256`, updater journal, checksum fetch, and authenticated update status. A failed health check must roll back; never force a partly unpacked build live. Resolve disk pressure only after a verified backup; repair clock synchronization before retrying expiry-sensitive operations.

For host loss, provision from the released package, restore the approved restricted state backup, configure the protected admin-key drop-in, and verify liveness before switching address. An absent backup means an honest loss of waypoints/registrations; never reconstruct positions or pings.

## Privacy or diagnostic incident

1. Disable `TARKOV_COMPANION_INTERNAL_TELEMETRY` and prevent further export.
2. Revoke implicated diagnostic/admin tokens and preserve minimum sanitized metadata.
3. Determine whether a prohibited field left the device/relay without reproducing it with real evidence.
4. Delete affected local diagnostic scope through the reviewed operation when retention obligations permit, and report the actual outcome.
5. Add a closed-schema/redaction test before re-enabling telemetry.
