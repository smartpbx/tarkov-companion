# ADR 0010: Supervise incremental runtime work and durable commands

Status: Accepted — 2026-09-15

## Context

The v1 runtime starts several services as one sequence, tracks background operations with
unstructured tasks, and queues raid-history writes as delegates. A slow or failed optional
feature can therefore delay unrelated readiness, cancellation can race a late result into
current state, and a queued write has no stable identity that a persistent store can lease or
deduplicate after restart. V2 adds concurrent recognition, paired-device, data-refresh, and
model workloads, so those implicit lifecycle and ordering rules are no longer sufficient.

## Decision

Introduce platform-independent execution primitives in Application. Each feature declares a
validated identity, dependencies, criticality, and lifecycle operation. A coordinator advances
the dependency graph incrementally, publishes immutable revisioned snapshots, isolates optional
feature faults, and exposes explicit degraded or failed state. Shutdown cancels admission,
requests feature stop in reverse dependency order, and awaits bounded completion before resources
are disposed.

Run background work through a bounded supervisor with separate interactive and maintenance
admission. Capacity is reserved for interactive work, but bounded fairness prevents sustained
interactive traffic from starving admitted maintenance work. Every operation has a typed policy,
deadline, cancellation ownership, and sanitized fault. Retry and circuit behavior consume
`TimeProvider`, making delay, recovery, and half-open transitions deterministic in tests.

Use generation tokens for latest-wins work. Starting a newer generation invalidates an older
result; completion may publish only through an atomic generation check. Cancellation alone is
not treated as sufficient because a dependency can return after ignoring or racing cancellation.

Replace delegate persistence queues with typed, versioned commands carrying operation and
aggregate identity. Processing is leased and at-least-once. A target must make operation IDs
idempotent. Commands are ordered within an aggregate: a poison or dead-lettered head blocks
later commands for that aggregate until an explicit retry or resolution, while unrelated
aggregates continue. Enqueue success is the publication boundary; an enqueue failure cannot be
reported as a saved domain change.

This change includes an in-process bounded fixture store only. #270 owns the SQLite implementation,
migrations, crash/restart durability, retention, and target-side operation ledger. #271 moves
capture producers onto the supervisor, and #294 owns final composition and application-wide
shutdown wiring.

## Alternatives considered

- Keep raw `Task` tracking and rely on callers to cancel correctly. This cannot prevent late
  publication or make resource and fault state consistently observable.
- Use one FIFO queue. It permits maintenance work to exhaust interactive capacity, while strict
  interactive priority can starve maintenance indefinitely.
- Continue queuing delegates. Delegates cannot be versioned, inspected, leased, replayed, or
  persisted safely across process boundaries.
- Allow later commands past a poison aggregate head. That can materialize history or inventory
  state out of order and makes recovery nondeterministic.
- Put the durable SQLite store in this change. Persistence and migration ownership belongs to
  #270, and combining it here would couple lifecycle semantics to one storage implementation.

## Consequences

Startup can expose usable features while optional work is still starting, failures are visible
without collapsing unrelated capabilities, and tests can deterministically exercise races,
timeouts, fairness, retry, lease expiry, and shutdown. Consumers must now declare operation
policies and handle explicit admission or degraded outcomes. Durable store implementations must
honor the frozen command, lease, idempotency, and aggregate-head contracts rather than merely
persisting a callback queue.

The supervisor adds bookkeeping and does not itself make an operation safe or durable. Final
composition, process-restart persistence, and feature-specific cancellation remain required in
their owning issues. None of these execution primitives admits EFT memory access, injection,
packet inspection, generated gameplay input, live-player tracking, or an in-game overlay.
