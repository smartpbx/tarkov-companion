# ADR 0019: One durable runtime head for potential loot spawns

## Status

Accepted for incremental V2 delivery.

## Context

The governed loot importer, production tarkov.dev normalizer, restart-durable publication store,
and typed high-value map layer existed independently. The desktop composition did not construct
them, startup did not restore a publication, and the renderer fixture was the only consumer of the
typed layer. A valid publication therefore could not reach an installed desktop process.

The monotonic publication policy also intentionally rejects shrinking coverage and removed item
evidence. That protects the last-known-good head from truncation, but a legitimate wipe or reviewed
upstream removal needs a controlled way to establish a new baseline without weakening ordinary
automatic refresh.

## Decision

- `AppComposition` constructs one mode/language-scoped production refresh adapter, durable
  publication store, runtime source, and typed layer projector.
- Startup restores the durable last-known-good head before background refresh. Offline startup
  performs no source request and can still project that head.
- The shared game-data refresh first updates the exact maps/items response cache. Loot refresh then
  reads those current cached documents without forcing duplicate endpoint downloads, normalizes
  them with the reviewed map catalog, and atomically advances the runtime head only after durable
  publication succeeds.
- The runtime source exposes immutable bundle state and builds `HighValueLootLayerResult` from the
  selected map ID, transform version, bounds, floors, evaluation time, and filter. Existing layer
  guards withhold absent, stale, mismatched, out-of-bounds, or unresolved evidence.
- Automatic publication remains monotonic. It cannot remove maps, records, candidate evidence, or
  move source chronology backwards.
- A separate reviewed-replacement API may bypass only coverage and item-removal ratchets. It
  requires the exact current content SHA-256, reviewer identity, reason, and UTC authorization.
  Source authority, import/generation/data-through chronology, and same-generation conflict checks
  still apply.
- The durable store records each exact replacement authorization in a bounded, atomic journal
  before attempting the publication write. A stale expected hash loses the inter-process race and
  cannot replace the new head. Normal refresh never calls this API.

## Consequences

- A desktop restart and an offline restart consume the same validated publication rather than a
  fixture or an empty process-local store.
- A missing or corrupt cache degrades to the typed unavailable layer; it does not manufacture
  markers or clear an already loaded in-process head.
- Patch/wipe removals require an explicit reviewed action and leave durable evidence, while silent
  shrink remains refused.
- Issue #286 still owns composition of the returned layer into the production Raid scene, and the
  paired tablet host remains a later consumer. This decision supplies their production source; it
  does not claim those surfaces are complete.
