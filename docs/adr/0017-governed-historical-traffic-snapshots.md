# ADR 0017: Govern historical traffic as immutable signed snapshots

Status: Accepted — 2026-09-16

## Context

Historical traffic can improve route planning, but an undocumented pile of observations would be
unsafe and irreproducible. It could mix maps or wipes, leak one contributor across train and test,
retain private identities, accept fields that imply live tracking, or let a corrupt download
replace the last usable offline model. The v2 evidence contract says historical and modelled
claims retain lineage, coverage, time, confidence/calibration, and model version. It does not by
itself define dataset consent, reproducible training, signing, or installation.

Issue #275 owns runtime inference and presentation. This decision must give that workstream one
immutable input without moving route decisions or a competing inference implementation into the
data pipeline.

## Decision

Historical traffic uses closed Core contracts under `Domain/Strategy/Data`. A source declares one
of four categories, matching provenance, licence, consent/terms, collection method, allowed-use
allowlist, review time, and exact map/game-version/mode/wipe/cohort scopes. Private feedback is explicit local
input with a versioned consent grant, named bounded spatial reference, historical phase window,
observation class, and evaluated prediction. No submission differs from explicit `Unknown`.

Feedback changes are append-only. Corrections point to the immediately prior revision and retain
source and consent; revocation is a terminal value-free event. Deletion removes the complete local
private chain. Published datasets, reports, models, and predictions are immutable. Changed or
revoked input appears only through a newly built version. The private journal uses expected-revision
appends, strict bounded restart parsing, an exclusive writer lease, and atomic file replacement; it
has no enumeration or export surface, and deletion retains no journal tombstone.

Normal installation is monotonic in the signed manifest's data-through and generation timestamps.
It refuses conflicting content at one generation and replay of an older valid package without
moving either installed head; explicit rollback is the authorized downgrade path. The detached
signature envelope's signing timestamp is informational because the manifest signature does not
bind that field, so freshness decisions never use it.

Partition assignment hashes a complete correlation-group id with a versioned policy and public
salt. Train, tune, and held-out shares are positive basis-point ranges. Imported labels are
recomputed, group leakage is rejected, and per-partition counts and hashes reconcile. Held-out
data is therefore structurally distinct from training/tuning input. Published group and record
ids are canonical digests produced only after identity removal; a plain hash of a raw player or
profile identifier is not accepted as de-identification.

The deterministic builder sorts inputs and accepts every timestamp and version explicitly. It
emits a dataset, build report, manifest, and opaque model artifact. The report includes
data-through/generated UTC, sample size, calibrated confidence, coverage and gaps, transform/model versions,
partition evidence, and the leakage result. Exact binding across all three JSON documents prevents
metadata from describing a different artifact.

The manifest is signed with ECDSA P-256/SHA-256. Import is byte/depth bounded and uses a strict
traffic JSON reader that rejects unknown fields. It validates semantic content and partition
digests, every cross-document binding, the artifact/report hashes, signature, and exact requested
compatibility before installation. Failure stores only a bounded quarantine receipt and cannot
move current or last-known-good heads.

Verified package files are staged and content-addressed before atomic head publication. A newly
accepted package becomes current and the previous verified current package becomes last known
good. Load revalidates local bytes and rolls back atomically when current is corrupt. Installed
snapshots work without a network connection.

## Consequences

There is no production dataset merely because the schema exists. A source cannot enter a build
until its licence, consent, provenance, uses, compatibility, collection method, and coverage are
reviewed. Raw local history stays private and cannot fit into a distributable aggregate record.
Every revocation or correction that affects contribution requires a new reproducible version.

Strict unknown-field rejection intentionally differs from the general same-major v2 reader. That
costs forward compatibility at the hostile import boundary; a schema change therefore requires an
explicit version change and reader update instead of silently dropping a possibly prohibited
field.

Model artifacts need a managed signing key and trusted public-key configuration. Signing keys and
raw feedback are operational inputs, never repository assets. Exact compatibility may yield no
answer where a broader heuristic could produce one; that is the required `unknown` behavior.

The runtime receives only a verified immutable artifact and its complete evidence envelope. It
still owns inference and must label output historical/modelled/predicted, never live detection.
