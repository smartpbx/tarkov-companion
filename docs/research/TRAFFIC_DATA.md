# Historical traffic data governance

Verified 2026-09-16. This document describes a contract and build path, not a bundled traffic
dataset. The repository currently publishes no production historical-traffic model and collects
no traffic feedback automatically.

## Boundary

Traffic data is about past, bounded aggregate conditions. It may name a catalog map, a named
region or corridor, a raid phase or bounded elapsed-time window, game mode, wipe, and cohort. It
cannot name a player, exact coordinate, current raid, current position, or live feed. Compatibility
also names the exact game version rather than treating a wipe as a version proxy. It never
comes from process memory or EFT network traffic. A runtime consumer may present it only as
historical, modelled, or predicted evidence, never as a sighting or detection.

The data pipeline ends at a signed immutable runtime snapshot. Issue #275 owns inference, route
generation, and presentation. This pipeline does not choose a route and cannot create a second
runtime inference path.

## Source inventory

Every source records its stable id, display name, category, explicit visibility, evidence
provenance, licence id, consent basis, collection method, allowed uses, exact compatibility scopes, review time, and
optional reference. A source without any one of those required fields is ineligible.

| Category | Required evidence class | Consent or terms | Required visibility rule |
| --- | --- | --- | --- |
| Static public facts | `PublicStructuredData` | public-data terms | distributable when licence permits |
| Reviewed curated knowledge | `CuratedData` | reviewed terms or consent | only the reviewed allowed uses |
| Historical aggregate | `HistoricalAggregate` | reviewed terms or consent | aggregate only |
| Private local feedback | `UserEntered` | explicit local opt-in | private local only |

An allowed-use list is an allowlist, not descriptive text. Train, tune, and held-out rows require
`ModelTraining`, `ModelTuning`, and `HeldOutEvaluation` respectively. A distributable dataset also
requires `DistributableSnapshot`. Raw private feedback can allow aggregate contribution but can
never authorize distribution of the raw record.

The inventory is deliberately empty in source control: no reviewed production traffic source or
model artifact ships today. Adding one requires a real licence and consent review, measured
coverage, a new immutable dataset/model version, attribution where required, and the signed build
files below. Raw private histories, signing keys, tokens, captures, and identities must never be
committed.

## Private feedback lifecycle

Feedback is explicit and local. Its enclosing local workspace may scope it to a profile, but the
feedback wire record itself carries no profile or player identity. It binds a source, an explicit
consent grant and notice version, one compatibility scope, one named region or corridor, one
historical phase window, the prediction being evaluated, and one observation class:

- `Contact`, `NoContact`, and `Avoided` are explicit assertions.
- `Unknown` is an explicit response that the user could not classify.
- no submission is absence and is never converted to `Unknown` or `NoContact`.

The private source and provenance identifier is the fixed non-identifying
`local-traffic-feedback`; it cannot be replaced with a nickname or account id.

The event chain begins at revision one with `Submitted`. A correction retains the feedback id,
source, and consent grant and appends a revision pointing to the immediately previous event. A
revocation appends a value-free terminal revision. Revisions are contiguous, event ids are unique,
and time cannot run backwards. Local deletion removes the complete private chain. Contribution or
revocation causes a later aggregate/model build with new versions; it never edits a published
dataset, model, prediction, or report in place.

Aggregation must remove the local profile id and retain only a sufficiently broad named
region/corridor and compatibility cohort. The distributable schema has nowhere to store identity,
exact coordinates, or current-raid state, and each distributable aggregate cell requires at least
five contributing samples. A stricter reviewed source threshold may raise that floor.

## Deterministic partitions and leakage

The split unit is `PartitionGroupId`, a canonical SHA-256 digest representing the complete
correlation group chosen by the reviewed transform (for example, all derived cells from one
contributing raid or one already-grouped public aggregate). Aggregate record ids are digests too;
neither field can retain a raw raid, profile, or contributor id. Rows from one split unit may never
cross partitions.

The reviewed upstream transform creates those digests only after removing contributor/profile
identity. A plain hash of a raw player, profile, or account identifier is not de-identification and
is ineligible; any source-local correlation mapping or privacy key stays outside the published
dataset and is governed with the private source material.

Assignment is independent of input order. The builder hashes UTF-8 text consisting of this domain,
policy version, public assignment salt, and group id separated by line feeds:

```text
TarkovCompanion.TrafficPartition/v1
<policy-version>
<assignment-salt>
<partition-group-id>
```

It reads the first SHA-256 word as an unsigned big-endian integer, reduces it modulo 10,000, and
applies the recorded train/tune/held-out basis-point boundaries. The current contract requires
three positive shares totalling 10,000. Imported partition labels are recomputed. Each partition
also carries record count, sample count, and a digest of its canonically ordered records. The
dataset rejects a group crossing partitions, a label that differs from deterministic assignment,
or counts that do not reconcile. Model selection may use train and tune only; held-out evidence is
reserved for evaluation and calibration.

## Reproducible build

`TrafficModelBuilder` takes all clocks, ids, versions, policy, sources, records, coverage, gaps,
and artifact bytes explicitly. It does not read the clock, network, game, filesystem, or random
state. Sources and records are ordinally sorted before serialization. Its dataset content digest
is SHA-256 over the canonical dataset content with the self-referential `contentSha256` field
omitted. Partition digests are SHA-256 over each canonical ordered record array.

One build emits these immutable files:

| File | Binding |
| --- | --- |
| `dataset.json` | source inventory, compatibility, partition policy, rows, partition digests, gaps |
| `build-report.json` | data-through/generated UTC, sample size, calibrated confidence, coverage, gaps, transform/model versions, partitions, passing leakage check |
| `manifest.json` | dataset/report/artifact digests plus exact compatibility, calibrated confidence, coverage gaps, and evidence summary |
| `manifest.signature.json` | key id, manifest digest, signing UTC, ECDSA P-256/SHA-256 signature |
| `model.artifact` | opaque versioned artifact consumed by #275 |

The report, manifest, and dataset must agree exactly on ids, versions, times, digests, partitions,
coverage, gaps, calibrated confidence, transform, and compatibility. A mismatch is quarantine, not a warning.
The CLI in `tools/TrafficModelBuilder` requires an explicit ISO UTC signing time so a repeated build
does not silently acquire the wall clock. Private signing keys are inputs and are never written to
the output package. Dataset, report, manifest, and model bytes are reproducible from identical
inputs; ECDSA signature bytes may differ because signing uses a fresh cryptographic nonce, while
still authenticating the identical manifest digest.

## Import, installation, and offline use

Traffic imports use their own strict JSON options. Unlike the forward-compatible general v2
reader, the traffic reader rejects every unknown property. This is intentional: a payload cannot
attach `playerId`, `currentRaid`, or exact coordinates and have those prohibited fields silently
discarded while the rest is accepted.

The default decoded limits are 16 MiB dataset, 2 MiB report, 256 KiB manifest, 16 KiB signature,
and 64 MiB artifact, with JSON depth limited to 32. Imports reject empty/oversized members,
unknown or duplicate properties, malformed contract values, unsupported schema/enums, non-finite numbers, content or
partition hash mismatch, inconsistent evidence, bad signatures, and missing exact compatibility.
Hostile bytes are not installed. Quarantine retains only a bounded receipt containing a digest
when the complete bounded package was readable, a reason code, disposition, and local UTC time;
it does not preserve the hostile body.

Verified packages are staged beneath the local snapshot root and moved into a content-addressed
version directory before one atomic state-file replacement publishes the new head. The previous
verified current head becomes last known good. A refused or incompatible package cannot move
either head. Startup revalidates the current package entirely from local files; if it is corrupt,
the store revalidates and atomically selects last known good. Explicit rollback uses the same path.
No network access is required to load or roll back an installed snapshot.

The local store retains at most four verified version directories by default and never prunes the
current or last-known-good head; operators may set an explicit bound from two through sixty-four.
Quarantine receipts have a separate default bound of 128. Neither retention path stores rejected
package bodies or raw private feedback.

Compatibility is exact. Map, game version, mode, wipe, and cohort have no wildcard value. If the installed
manifest lacks the requested cell, the consumer receives unavailable/unknown coverage and may
fall back to a compatible last-known-good snapshot without moving the global current head. It must
never generalize from another map, game version, mode, wipe, or cohort.

## Verification gate

Unit coverage fixes source/consent/use rules, explicit unknown feedback, full correction and
revocation chains, immutable caller collections, deterministic group assignment, partition/count
reconciliation, strict hostile-field rejection, signature/integrity checks, compatibility
refusal, quarantine head preservation, order-independent builds, and last-known-good rollback.
GitHub Actions is the required execution gate; no local .NET workload is evidence for integration.
