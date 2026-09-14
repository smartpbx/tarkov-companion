# Recognition corpus and benchmark contract

This is the governed evaluation boundary for recognition work. It is deliberately separate from
the normal scan pipeline: normal capture retains decoded pixels only in memory for the scan and
then discards them. The corpus is a deliberate, consented workflow outside every Git repository,
worktree, build artifact, telemetry stream, support bundle, and package.

No corpus workflow may read EFT process memory, inject or hook code, inspect or decode EFT
traffic, generate gameplay input, automate any game action, track players, create ESP/radar, or
render an in-game overlay. It evaluates user-provided visible captures only; its predictions are
evidence, never live detections.

## Versioned interchange

The checked-in schemas under `fixtures/recognition-corpus/schemas/` are the v1 contract:

- `consent.v1` records a private consent hash, allowed use, retention expiry, and revocation
  state; no contributor identity is in the interchange.
- `privacy-review.v1` records an approved/rejected/pending human review and redaction outcome.
- `provenance.v1` records immutable capture intent, session and correlation identities, ordered
  ordinal, workspace/profile/map/floor, plan/objectives, selection/prior scan reference,
  initiating device and surface, resolution, UI scale, locale, and game/UI version.
- `manifest.v1` is private truth. It records canonical and independently observed decoded-pixel
  SHA-256, the versioned perceptual-near-duplicate graph, full private consent/privacy authority,
  lineage, bounded truth regions, and truth states `known` or `unknown`. The checked-in schema is
  public; populated manifests and their private-evidence section are not.
- `run-plan.v1` and `predictions.v1` are the truth-free producer handoff for #299 and #273.
  They contain no labels, truth, filenames, paths, pixels, consent records, OCR strings, or
  per-sample outputs eligible for GitHub publication.
- `thresholds.v1` is the frozen scorer-policy ownership point for #272. Recognizers must not
  tune or override its policy version, thresholds, or minimum denominators.
- `aggregate-results.v1` is the only format which may leave the private scorer, and only after
  its privacy aggregation rule is satisfied.

Readers ignore unknown JSON properties for compatible evolution. They still reject malformed or
empty documents, missing required fields, malformed hashes, undefined enums, non-opaque IDs,
filesystem names/paths, labels in a truth-free interchange, and every real-raster eligibility
failure.

## Consent, privacy, retention, and revocation

A real raster is eligible only when all of the following are true at score time:

1. An explicit full private consent record has a matching SHA-256 evidence hash, a valid UTC
   consent time, and permits `benchmark`.
2. Its UTC retention expiry has not passed and its revocation state is `active`.
3. A matching full human privacy record has an approved UTC review, with redaction either
   `not-required` or `applied`.
4. The canonical decoded-pixel hash matches the independently observed imported-content hash and
   the source is outside every repository/worktree.

Revocation, expiry, rejected review, a changed pixel hash, a hash-shaped summary without its full
private record, or a missing record removes the sample from eligibility and invalidates the
affected split lock/baseline. Ingest never accepts an
original filename or an absolute/local filesystem path; the CLI rejects a manifest path nested
under `.git` or a worktree. A consent hash is a correlation check, not a replacement for the
private consent record and human review.

## Split and lineage

Canonical decoded-pixel SHA-256 is computed on decoded pixel bytes, not a container filename or
encoded file bytes. `phash-graph.v1` additionally connects perceptual near duplicates. The split
planner takes connected components across exact hashes, near-duplicate links, and a capture
sequence; it derives a component ID from every sorted content root and assigns train/tune/test
from a SHA-256 bucket (80/10/10). Thus originals, bursts, crops, redactions, and all scroll frames
stay in one unit. The manifest's declared split unit is independently recomputed and a mismatch
or cross-split graph edge fails validation.

A truth-free plan cannot prove that private graph on its own. `PrivateRunPlanner` therefore takes
the validated private manifest, orders every authorized sample canonically, recomputes each split,
copies the exact bounded context and lineage, and emits a content-derived plan lock. Run-plan
validation requires the private manifest again and recomputes the entire plan and lock; it rejects
missing, duplicate, extra, or reordered samples and any context, lineage, intent, evidence-class,
split, graph-version, policy-version, or lock change. A lock-shaped string without that private
context proves nothing.

Lineage keeps `sequenceId`, frame ordinal, viewport identity, container identity, optional parent
container identity, and overlap with the preceding frame. Frames are contiguous and ordered;
failed or missing frame evidence is reported, not renumbered. A repeated `truthId` across
overlapping frames means one real claim: the scorer verifies repeated truth is consistent,
deduplicates it before denominator calculation, and reports duplicate producer claims as false
positives rather than counting it twice. Truth and prediction rectangles use explicit integer
`x`, `y`, `width`, and `height` and must fit the capture dimensions without overflow.

## Producer and scorer boundary

1. #272 creates the truth-free run plan from private eligible material.
2. #299 and #273 return typed context, region, grid/cell, item, attribute, extract/timer,
   health, or recommendation predictions without test truth.
3. The independent #272 scorer joins predictions to private truth and publishes only safe
   aggregates.
4. #301 is the only human gate for consented real captures and a measured current-main baseline.

The scorer never pools `real-raster`, `synthetic-raster`, and `post-ocr-evidence`. For every
intent — Loot decision, Full stash, Ammo, Keys, Quest/future-quest items, Map/extracts/timers,
Health/character, and Auto-detect — it emits a separate evidence-class slice. Each slice includes
the original numerator/denominator, excluded unknowns, independent split units, attempted eligible
truth coverage, accuracy, recall, false-positive rate, F1, each applicable metric numerator and
denominator, a 95% Wilson recall interval, abstention, confident-wrong, sequence
missing/reordered-frame and overlap/dedup errors, and available performance. A repeated truth may
match once from any frame where it remains visible. Every unsupported or underpowered slice is
`insufficient-data`; powered slices are explicitly `pass` or `fail` against the frozen candidate
coverage, abstention, confident-wrong, and F1 thresholds.

For this claim-set benchmark, coverage is attempted eligible truth divided by eligible known truth;
an explicit detected or abstained result of the matching type is an attempt, while missing or
unavailable output is not. Accuracy is `TP / (TP + FP + FN)`, recall is `TP / (TP + FN)`, the
false-positive rate is `FP / (TP + FP)` because the corpus has no meaningful true-negative claim
universe, and F1 is `2TP / (2TP + FP + FN)`. Each denominator is emitted beside its numerator.

The candidate values in `thresholds/recognition-release.v1.json` are frozen before tuning. They
are deliberately candidate policy, not an accuracy claim. An aggregate may be committed only when
it is privacy safe and has at least five independent split units; otherwise it remains
`insufficient-data`.

## Current baseline

`fixtures/recognition-corpus/baselines/current-main.v1.json` is intentionally exactly
`not-measured: blocked-no-consented-pixels`. Synthetic rendered scenes and post-OCR fixtures are
useful contract coverage but cannot change that status or establish real-raster accuracy. #301
must obtain actual consented captures, complete human privacy review, preserve the untouched test
split, and run the frozen scorer before a measured baseline can exist.

## Local use and validation

`tools/RecognitionCorpus` is a small standalone .NET tool. It validates a truth-free run plan or
prediction file only when the file is outside a repository/worktree:

```text
dotnet run --project tools/RecognitionCorpus -- validate-manifest /private/corpus/manifest.json
dotnet run --project tools/RecognitionCorpus -- validate-run-plan /private/corpus/run-plan.json /private/corpus/manifest.json
dotnet run --project tools/RecognitionCorpus -- validate-predictions /private/corpus/predictions.json /private/corpus/run-plan.json
```

The second argument to run-plan validation is the authorized private planner context. The second
argument to prediction validation is the exact truth-free plan whose run, producer, membership,
intent, evidence class, dimensions, and timing contract the output must match.

The test project covers positive, negative, hostile, privacy, split/leakage, region-bound,
prediction-mismatch, schema-compatible unknown-property, metric, threshold, and sequence cases.
Both projects are registered in the solution so the normal Linux and Windows jobs execute them;
GitHub Actions stays the required substantive-change gate.
