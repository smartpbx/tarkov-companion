# ADR 0011: Publish V2 data through an evidence-preserving local platform

Status: Accepted — 2026-09-15

## Context

The v1 database mixes replaceable upstream cache documents with normalized catalog rows and
locally owned player state. A refresh has no atomic dataset publication identity, the cache
duplicates response bodies, and destructive migrations have no verified restoration contract.
V2 also introduces the durable runtime outbox, multiple profile contexts, nested observed
inventory, planning, craft history, and historical model outputs. Those records must preserve
unknown values and provenance without ever implying live game access or live detection.

## Decision

Use SQLite as one local transaction boundary, with distinct tables for disposable transport
bodies, published catalog metadata, normalized query models, and locally owned evidence.

- Store upstream UTF-8 bodies once by SHA-256, gzip-compressed. Cache keys carry validators and
  reference a body; cache byte, entry, and age budgets evict only transport bodies. Dataset
  publications retain the content hash as provenance even after an evicted body is gone.
- Record every endpoint attempt as `current`, `stale`, `refused`, or `partial`. Advance visible
  and last-known-good heads only according to that state, in the same transaction as sync state.
- Preserve absent measurements as `NULL`, unresolved upstream identifiers as explicit rows, and
  forward-compatible source or producer fields as bounded JSON. Reject empty, unexpectedly
  shrunken, oversized, deeply nested, non-finite, or required-field-deficient input before a
  publication head changes.
- Keep profile progress normalized and compare-and-swap its workspace revision. Keep observed
  inventory, raid fields, craft records, plans, retention choices, and model results with source
  and UTC evidence timestamps; manual and observed facts remain separate. Model records require
  data-through/generated provenance, coverage/confidence/calibration when known, and a model
  version, and may never be labelled live.
- Implement the runtime outbox with durable aggregate sequences, fenced leases, explicit
  dead-letter resolution, bounded retention, and a target operation ledger committed in the same
  transaction as each raid-history side effect.
- Make the migration ledger explicit and pair every upgrade with a rollback fixture. Before a
  destructive step, create and verify a SQLite-consistent recovery copy; a failed migration
  restores it atomically or reports the still-verified recovery path.
- Expose inspectable, cancellable cache cleanup and database prune/reindex/vacuum APIs. Scheduling
  records intent and outcomes in SQLite; dry-run remains non-mutating.

Raw endpoint bodies come only from public `json.tarkov.dev` HTTPS responses. The platform does
not read game memory, inject or hook code, inspect game network traffic, generate input, automate
inventory actions, track live players, or persist screen captures.

## Alternatives considered

- Keep one JSON blob per cache key. This wastes first-sync space and makes identical documents
  impossible to deduplicate or inspect by content identity.
- Treat the newest attempted refresh as current. A malformed or partial response would replace
  known-good catalog state and make offline behavior nondeterministic.
- Encode unknown numeric values as zero or stamp missing source times with import time. Both
  manufacture facts the source did not publish and erase the distinction between unknown and
  measured zero.
- Back up SQLite with a file copy or continue after backup failure. WAL files make a byte copy an
  unsafe consistency boundary, and a destructive migration without a verified recovery copy can
  destroy the only local player history.
- Store outbox callbacks or acknowledge a target side effect separately. Callbacks cannot survive
  restart, and separate acknowledgement permits an uncertain success to replay the side effect.

## Consequences

Catalog readers get atomic visible and last-known-good identities, bounded offline cache behavior,
and nullable observations whose uncertainty survives round trips. User state and historical model
evidence are queryable without coupling Core to SQLite or the filesystem. The schema and recovery
path are larger and every new migration must maintain an upgrade/rollback pair, the unread-schema
ratchet, query-plan evidence, and deterministic failure fixtures.

Cache cleanup may remove the compressed body for an old publication, so the publication hash is
provenance rather than a permanent body foreign key. Models remain historical or predictive
claims with explicit source and timestamps, never live detections. GitHub Actions is the required
integration gate for the migration, hostile-input, durability, and performance fixtures.
