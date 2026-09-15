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
feature faults, and exposes explicit degraded or failed state. Hard dependencies settle before a
dependant starts. Optional dependencies never gate startup, including within one priority phase;
if one later fails or is degraded, that state propagates through already-running dependants.

### Shutdown is bounded and truthful

Shutdown cancels admission, requests feature stop in reverse dependency order, and awaits
bounded completion before resources are disposed. A bound that expires is reported, never
converted into a terminal state:

- A supervised operation whose dependency ignores cancellation stays `Running`, keeps its
  completion unset, and keeps its exact resource class (`Light`, `IO`, `CPU`, or the exclusive
  slot) until the user work returns. `StopAsync` reports how many operations are unfinished.
  An attempt timeout follows the same rule: the deadline is reported at once, but the slot is not
  reused and no retry starts while the timed-out invocation is still running.
- Startup, background, and manual data refresh calls join one supervised operation. A manual
  caller's cancellation or deadline bounds only that caller's wait; the refresh remains observed
  and keeps its IO admission and refresh lock until every dependency task actually returns.
- Disposal after a timed-out stop leaves the already-cancelled synchronization objects alive for
  the late completion, and a composition root disposes a lock only when nothing still owns it. The
  application startup coordinator begins lifecycle and supervisor stop together under one
  ten-second window and does not add a second supervisor-disposal wait after that bound expires.
- There is no `StopTimedOut` state in either state machine.

Stopping and starting race safely. Every feature start publishes its invocation before any stop
can observe it, and once stopping has begun no feature may enter `Starting`. The shutdown walk
therefore cannot pass a feature whose start is in flight: it waits for that start to return,
stops the feature, and only then continues to that feature's dependencies. A start that exceeds
its deadline is reported as the non-terminal `StartTimedOut`, and the feature is stopped once the
callback finally returns before it settles as `Failed`.

Feature start and stop callbacks are scheduled before invocation. Their timeout is therefore
armed even when a callback enters synchronous SQLite or CPU work before it returns a task, and a
synchronous prefix cannot serialize otherwise independent feature starts on the coordinator.

### Scheduling is bounded and starvation-free

Run background work through a bounded supervisor with separate interactive and maintenance
admission. Capacity is reserved for interactive work. Fairness is bounded in two ways: a higher
priority may overtake the oldest eligible item at most `MaxPriorityBurst` times in a row, and a
pending `HeavyExclusive` item creates a sequence barrier. Maintenance work behind that barrier
stops while earlier invocations drain. Otherwise-idle reserved capacity remains available to
interactive work for as long as an earlier invocation is still running, including one that ignores
cancellation; once those original blockers return, later interactive work drains and the exclusive
item receives the next turn. This preserves contextual screenshot availability without letting a
continuous stream of small work keep an eligible exclusive item out for ever. Every operation has
a typed policy, deadline, cancellation ownership, and sanitized fault. Dependency callbacks are
scheduled before invocation so synchronous prefixes cannot prevent a deadline being armed. Retry
and circuit behavior consume `TimeProvider`, making delay, recovery, and half-open transitions
deterministic in tests. Cancellation registrations are unregistered, never disposed, while the
scheduler lock is held.

Restart modes mean exactly this:

| Mode | Behavior |
| --- | --- |
| `Never` | Never restarted. |
| `Manual` | Never restarted automatically; a caller may `Retry` with a new operation id, and only for idempotent work. |
| `OnFailure` | A run that faulted or timed out is queued again after a backoff; success, cancellation, caller cancellation, or supervisor stop ends it. |
| `Always` | As `OnFailure`, and a successful run is also queued again. |

Automatic restarts require an idempotency guarantee, keep one operation id, count their
restarts, use the policy's exponential retry delay floored at the supervisor's
`MinimumRestartDelay` on injected time, and continue to hold their admission while they wait.
Caller `Retry` is refused for `OnFailure` and `Always`, which the supervisor restarts itself.

### Latest-wins work

Use generation tokens for latest-wins work. Starting a newer generation invalidates an older
result; completion may publish only through an atomic generation check. Cancellation alone is
not treated as sufficient because a dependency can return after ignoring or racing cancellation.
Invalidation starts cancellation outside the generation lock and returns without waiting for
callbacks; callback tasks and token sources remain owned and observed until they settle.

### Durable commands

Replace delegate persistence queues with typed, versioned commands carrying operation and
aggregate identity. Processing is leased and at-least-once. A target must make operation IDs
idempotent. Commands are ordered within an aggregate: a poison or dead-lettered head blocks
later commands for that aggregate until an explicit retry or resolution, while unrelated
aggregates continue. A lease that expires counts as an attempt, so a command whose handler
crashes dead-letters once its attempt budget is spent instead of replaying for ever. The
processor serializes batch admission and bounds handler fan-out. A deadline reports without
releasing its operation-keyed owner: renewal and observation continue until that exact handler
settles, while unrelated aggregates may make progress in later passes. If ownership becomes
unknowable, the settled invocation remains as a lightweight local fence rather than becoming
eligible for handler replay. Active owners and retained fences have explicit process bounds.
An explicit retry or terminal resolution releases that fence only after any retained invocation
settles and the store has made the operator's reconciliation decision durable.

Handler settlement and store acknowledgement are separate phases. Once a handler succeeds,
`CompleteAsync` is retried under the same lease and delivery token while heartbeats continue; the
handler is not invoked again. The terminal compare-and-swap and heartbeat may overlap, with no
more than one of each call per operation at once, so a slow acknowledgement cannot suppress lease
renewal. Every complete, heartbeat, retry, and dead-letter mutation carries injected-clock time
and accepts only the exact unexpired lease token. A lost lease is published as acknowledgement
unknown rather than as a handler failure. #270's target-side operation ledger is the reconciliation
boundary if another process acquires that command after ownership becomes unknowable.
Late owner settlement wakes the delivery pump, so completion and health do not remain stale until
an earlier lease-expiry timer or unrelated acceptance fires.

Command kinds form a closed set with pinned numeric values. Raid history crosses the outbox only
as `RaidHistoryCommand` values — start, state, extracts, scan, sale, quest, position, end — each
encoded by its own reviewed codec into a record of plain values. There is no route for generic
event JSON: the outbox refuses `RecordEventAsync`, payloads are read back with unmapped members,
missing constructor arguments, and contract-violating nulls rejected, and free text that names a
screenshot file is refused before acceptance. A position crosses as typed coordinates without its
screenshot filename; delivery stores a stable per-command token in the filename's place.

Acceptance is the publication boundary:

- All commands produced by one observed transition are accepted by one all-or-nothing store
  batch, so a refusal cannot leave part of a transition stored.
- Nothing fallible follows a successful store call before the caller is told, including status
  reads, cancellation checks, or lock release after disposal. An acceptance that the store
  refused reports the sanitized fault as `LastAcceptanceFault`.
- Aggregate sequences are never reused, even after a store call that failed, so a later command
  cannot collide with one a durable store applied ambiguously.
- The raid coordinator works each durable transition out on a staged copy of raid state, has its
  commands accepted, and only then commits and publishes that copy. A refused transition leaves
  the live raid state exactly as it was. Direct v1 stores keep their show-first behavior.

Delivery health is runtime state. The delivery pump never ends on a store or processor fault: it
records a sanitized `LastPumpFault`, counts consecutive faults, backs off on injected time from
one to thirty seconds, and resumes by itself. `OutboxSnapshot` publishes counts, pump state,
the last successful pass, the acceptance fault, and a recovery-prioritized dead-letter window by
identity and fault (never payload) into `ApplicationRuntimeSnapshot.Outbox`. The manual recovery
seam is `RequestPumpRecovery`, which wakes the pump immediately or restarts one that ended, and
`RetryDeadLetterAsync`, which returns one dead letter to delivery. Health lists a bounded,
recovery-prioritized window — retryable rows first and oldest-first within each class — and
explicit resolution is the only way to release an expired or deliberately discarded aggregate
head; `RaidActivityCoordinator` exposes both recovery actions. Recovery mutations share the
acceptance admission gate, so shutdown waits for an admitted operator decision and a queued call
cannot mutate the store after shutdown has closed its wake signal. Because the supervisor,
lifecycle, and delivery pump all publish from background threads, `RuntimeStateStore` linearizes
each state replacement with its subscriber notification; the snapshot is still computed under its
own lock, and no subscriber runs while that inner lock is held.

Cancellation of an attempt that is still running is explicit. A wait can observe a caller's
cancellation before a linked token has passed it on, and tearing the link down afterwards used to
drop it, so the executor, outbox processor, and feature starts cancel an unfinished attempt
directly and keep its token source alive until the attempt and cancellation callbacks return.
When lifecycle startup itself is cancelled, its phase drains every already-launched feature-start
wrapper before the coordinator releases the shared startup token link; raw starts that ignore
cancellation remain owned by their compensating stop.
Cancellation delivery is asynchronous: a hostile callback cannot hold a timeout or invalidation
caller hostage.

This change includes an in-process bounded fixture store only. Its capacity bounds unresolved
work, including dead letters; completed rows have a separate bounded retention window. A dead
letter keeps its aggregate ordered and consumes admission until it is retried or explicitly
resolved. #270 owns the SQLite implementation, migrations,
crash/restart durability, retention, the target-side operation ledger, and durable aggregate
sequence allocation, which the process-local sequence here cannot provide across restart. #271
moves capture producers onto the supervisor, #281 owns presenting delivery health and the
recovery actions, and #294 owns final composition and application-wide shutdown wiring.

## Alternatives considered

- Keep raw `Task` tracking and rely on callers to cancel correctly. This cannot prevent late
  publication or make resource and fault state consistently observable.
- Report a stop that timed out as terminal. That frees a slot whose user work is still running
  and lets a second copy start beside it.
- Use one FIFO queue. It permits maintenance work to exhaust interactive capacity, while strict
  interactive priority can starve maintenance indefinitely.
- Continue queuing delegates. Delegates cannot be versioned, inspected, leased, replayed, or
  persisted safely across process boundaries.
- Carry raid events as a type name and a JSON string inside a typed payload. That bypasses the
  outbox privacy boundary for coordinates and screenshot names.
- Apply evidence to raid state before its record is accepted. A refused record then leaves state
  that the history never received, and later observations build on it.
- Allow later commands past a poison aggregate head. That can materialize history or inventory
  state out of order and makes recovery nondeterministic.
- Put the durable SQLite store in this change. Persistence and migration ownership belongs to
  #270, and combining it here would couple lifecycle semantics to one storage implementation.

## Consequences

Startup can expose usable features while optional work is still starting, failures are visible
without collapsing unrelated capabilities, and tests can deterministically exercise races,
timeouts, fairness, retry, restart, lease expiry, and shutdown. Consumers must now declare
operation policies and handle explicit admission or degraded outcomes, and shutdown callers must
read `SupervisorStopResult` and lifecycle snapshots rather than assume a bounded stop finished.
Durable store implementations must honor the frozen command, batch, lease, idempotency, retention,
and aggregate-head contracts rather than merely persisting a callback queue. A full or failing
store now stops durable raid transitions from being applied, which is visible in runtime state
instead of being hidden behind state that was never recorded.

The supervisor adds bookkeeping and does not itself make an operation safe or durable. Final
composition, process-restart persistence, and feature-specific cancellation remain required in
their owning issues. None of these execution primitives admits EFT memory access, injection,
packet inspection, generated gameplay input, live-player tracking, or an in-game overlay.
