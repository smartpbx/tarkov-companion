# Privacy

This companion is external and read-only toward Escape from Tarkov. It never accesses game memory, injects or hooks code, inspects EFT network traffic, creates gameplay input, automates gameplay, detects enemies, renders ESP/radar, or draws an in-game overlay. Diagnostics have the same limits.

## Data handling

| Material | Normal location | Diagnostic rule |
| --- | --- | --- |
| User-selected screenshots | Game screenshot folder | Never copied into diagnostics; pixels/crops are absent. |
| Feature-required game logs | Local game log folder | Never uploaded or attached to diagnostics. |
| Settings, profile, cached public data | Local application data | Diagnostics hold category-level operational state only. |
| Relay marks | Relay state directory | Backups never include live positions or pings. |
| Problem report | Relay reports directory | A public issue names a reference only; the body stays off GitHub. |
| Diagnostic event | #270-owned local store when wired | Closed, sanitized operational record only. |

Exact coordinates, names, credentials, screenshot names, full paths, OCR text/crops, report bodies, and raw log lines are prohibited diagnostic fields.

## Choice and control

Diagnostics work with central telemetry disabled. `TARKOV_COMPANION_INTERNAL_TELEMETRY=enabled` is a separate, explicit, revocable choice; it is never inferred from developer mode, a relay address, or a debug build. The user must be able to inspect destination, closed schema, retention preview, and export/delete controls before it transmits.

`TARKOV_COMPANION_DIAGNOSTIC_LOG_LEVEL` accepts `Trace`, `Debug`, `Information`, `Warning`, or `Error`; absent/invalid values use `Information`. More verbosity never permits raw evidence.

`TARKOV_COMPANION_DIAGNOSTIC_TOKEN` supports one current token and up to two expiring previous tokens:

```text
current-token,previous-token@2026-09-16T00:00:00Z
```

Tokens are at least 32 characters, use constant-time comparison, and never appear in an event, log, export, or report. A previous token must have a UTC expiry and is refused immediately after it. See [key rotation](runbooks/relay-operations.md#key-rotation).

Public relay health excludes readiness detail, room/member activity, report references, names, positions, and operator configuration. Detailed readiness is authenticated. The monitoring workflow prints neither relay URL nor health body.

Diagnostic export is local and reviewed; its preview shows scope before it writes. Delete confirms success/failure rather than claiming deletion after a storage error. Retention, preview, export, and delete use #270's persistence interfaces so there is one recoverable store and migration history.

The primitives in this branch await the repaired #268 runtime transplant and #294 composition.
They are not permission to add a second store or to wire telemetry before #270 supplies the single
durable adapter.
