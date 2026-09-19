# ADR 0020: Debug Capture is not built

## Status

Accepted (#309). Reversible: a later ADR may supersede it, and the last section says what that costs.

## Context

#309 asked for two separate things: tidying the game's own screenshot files, and **Debug Capture**, a way
to retain the pixels the app decodes so a bug can be reproduced. The first is built (a previewable,
dry-runnable, ledgered tidy that only ever moves game-named files to the recycle bin). The second was
never built: `AppDataPaths.DebugCaptures` named a folder nothing wrote to, and
`DataRetentionPolicy.DebugCaptureEnabled` is stored and never read.

The full version the issue describes needs explicit enablement with a purpose, an expiry, a size quota,
access control, a delete/revoke control, a preview and redaction step for every retained capture, and
separate per-purpose consent before any of it is shared. That is a retention store, a consent model and a
redaction tool.

## Decision

Do not build Debug Capture. Ordinary capture, recognition and scanning persist no decoded pixels, and there
is no switch that makes them.

## Why

- **Every pixel the app reads is already a file the player has.** The inputs are the game's own saved
  screenshots, a paste or drop, and a file pick. A second retained copy adds a store to secure and to expire
  and adds no picture the player cannot already attach.
- **A report that needs a picture can attach the original, deliberately.** That is a per-file choice at the
  moment of reporting, which is a smaller and safer consent than a standing capture mode.
- **The repository is public and the screenshots carry where the player was standing.** A retention store
  for them is an attack and privacy surface with no user-facing benefit today.
- **The recognition corpus is gathered another way.** It is collected by asking a player for specific
  screenshots, not by the app quietly keeping some.

## Consequences

- `AppDataPaths.DebugCaptures` is removed and a test pins that no such member exists.
- `DataRetentionPolicy.DebugCaptureEnabled` and `retention_policies.debug_capture_enabled` stay in the
  schema (removing a column is a migration owned by #270). Nothing reads them and nothing sets the flag; a
  test pins that turning screenshot tidying on leaves it off.
- Setup offers no Debug Capture control, and nothing says it exists.

## If this is reversed

It is a feature, not a flag: an ADR that states the purpose and the audience, a store with a quota and an
expiry, a redaction step that runs before anything is written, and per-purpose consent before anything
leaves the machine. The schema column above is a starting point, not a design.
