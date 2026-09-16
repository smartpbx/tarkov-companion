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
  per-sample outputs eligible for GitHub publication. Every prediction repeats the provenance and
  lineage of the plan sample it answers and states when and by what it was produced.
- `thresholds.v1` is the frozen scorer-policy ownership point for #272. Recognizers must not
  tune or override its policy version, thresholds, or minimum denominators.
- `aggregate-results.v1` is the only format which may leave the private scorer, and only after
  its privacy aggregation rule is satisfied.

Readers ignore unknown JSON properties for compatible evolution. They still reject malformed or
empty documents, missing required fields, malformed hashes, undefined enums, non-opaque IDs,
original-name fields, absolute paths, relative traversal strings, labels in a truth-free
interchange, and every real-raster eligibility failure. These privacy checks recurse into unknown
extension fields rather than trusting a known-property projection.

Property names are compared in a normalized form: Unicode compatibility-folded (NFKC),
lower-cased, and stripped of every character that is not a letter or digit. `original_filename`,
`source_file_name`, `Original-File-Name`, a full-width `filename`, and a name split by a zero-width
space are therefore the same name, as they would be to a reader that binds leniently; the earlier
check ignored case only and let the first two through. Some forbidden names match only whole
(`path`, `truth`, `consent`); the fragments `filename`, `filepath`, `originalname`, `originalpath`,
`originaluri`, `sourcepath`, `absolutepath`, and `ocrtext` are refused inside any longer name. A
truth-free interchange also refuses every consent and privacy-review member name (`consentHash`,
`allowedUses`, `retention`, `reviewHash`, and so on), so a record cannot be rebuilt field by field
under an extension. Two members of one object whose names normalize alike are duplicates, and a
property name shaped like a path is refused like a path-shaped value. Name matching is a tripwire,
not a content scanner: it cannot recognize a translated or homoglyph name (`nombreDeArchivo`, a
Cyrillic `а`), a name split across nested objects (`{"original": {"name": ...}}`), or a filename
carried as an ordinary string value. Values shaped like paths are still refused wherever they
appear, and nothing publishable carries extensions at all: emitted plans and published aggregates
are re-serialized from typed members.

An unknown property is tolerated; an unknown or misplaced *versioned* sub-document is not. v1
defines exactly where a nested `schemaVersion` may appear: `samples[].provenance`
(`provenance.v1`) in a manifest and a run plan, `predictions[].provenance` (`provenance.v1`) in
predictions, and `privateEvidence[].consent` (`consent.v1`) and `privateEvidence[].privacyReview`
(`privacy-review.v1`) in a manifest. Thresholds and aggregates define none. A declaration anywhere
else fails the whole document even when its value is a known version, as does the wrong known
version at a defined position, a non-string version, a case or separator variant of the name, or
a nested JSON Schema `$schema` or `$id` (a root `$schema` remains an editor hint).
The earlier check accepted any known value at any depth, so an extension in a run plan or in
predictions could carry a complete `consent.v1` record and pass as an ignorable unknown property.
Positions are compared as member chains, so an extension that merely imitates the path
(`futureExtension.samples[0].provenance`, or a member literally named `samples[].provenance`) is
not a defined position.

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
affected split lock/baseline. Ingest never accepts an original filename or an absolute/local
filesystem path. A consent hash is a correlation check, not a replacement for the private consent
record and human review.

Consent scopes producer access as well as scoring. A real capture keeps its content-stable split,
because moving it would break the unit it shares with its crops and scroll frames, but the
planner withholds it from a run plan when its split is Train and consent lacks `train`, or Tune
and consent lacks `tune`. Units are still built from every sample, so a withheld original keeps
joining its derivatives. Validation names a plan that carries a withheld capture, and withdrawing
a use later invalidates any plan that still hands it out.

## Private paths

Before reading or writing anything, the CLI canonicalizes the path one component at a time. A
link's raw target is spliced back into the components still to resolve, so the parents inside
every target are resolved as well as the final entry, and `..` removes only a component already
known not to be a link. The previous resolver asked the runtime for a link's final target, which
follows a chain on the last component but not a directory link inside that target: a private
link to `outside/hop/file`, with `hop` linking into a worktree, passed every check. Link loops,
missing components, and reparse points that are not symlinks or junctions fail closed, and error
messages carry no path.

- Private inputs (manifest, run plan, predictions) and private outputs (an emitted run plan) are
  rejected if the lexical or canonical path is inside a repository or worktree.
- Every private input and every output, including a published aggregate, is rejected inside Git
  storage: a `.git` component, or a separated or bare Git directory recognized by its `HEAD`,
  `objects`, and `refs`.
- An output must name a file in an existing directory, may not replace a link, directory, or
  reparse point, and may not overwrite any input. Writes go to a new `CreateNew` temporary file
  beside it and are renamed into place.

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
copies the exact bounded context and lineage, and emits a content-derived plan lock that also
commits to every canonically ordered truth id, state, kind, value, and bounded region. Decimals
enter the lock in canonical form, so `1.25` and `1.250` are one lock input, and list fields are
count-prefixed. Run-plan validation requires the private manifest again and recomputes the entire
plan and lock; it rejects missing, duplicate, extra, or reordered samples and any context, lineage,
intent, evidence-class, split, graph-version, policy-version, or lock change. A lock-shaped string
without that private context proves nothing.

Lineage keeps `sequenceId`, frame ordinal, viewport identity, container identity, optional parent
container identity, and overlap with the preceding frame. Frames are contiguous and ordered;
failed or missing frame evidence is reported, not renumbered. A repeated `truthId` across
overlapping frames means one real claim: the scorer verifies repeated truth is consistent,
deduplicates it before denominator calculation, and reports duplicate producer claims as false
positives rather than counting it twice. Truth and prediction rectangles use explicit integer
`x`, `y`, `width`, and `height` and must fit the capture dimensions without overflow.

## Producer and scorer boundary

1. #272 creates the truth-free run plan from private eligible material with `emit-run-plan`.
2. #299 and #273 return typed context, region, grid/cell, item, attribute, extract/timer,
   health, or recommendation predictions without test truth.
3. The independent #272 scorer joins predictions to private truth and publishes only safe
   aggregates.
4. #301 is the only human gate for consented real captures and a measured current-main baseline.

`predictions.v1` carries the `planLock` of the plan it answers. A run id is chosen by whoever emits
a plan and can be reused; the lock cannot, so predictions produced for a superseded plan are
refused by `validate-predictions` and by the scorer even under the same run and producer.

Each prediction also carries immutable producer traceability. `provenance` and `lineage` repeat
the plan sample's capture intent id, session and correlation ids, capture ordinal, bounded context
snapshot, and sequence, frame, viewport, container, overlap, and parent identity; validation and
the scorer compare every member with the plan sample by value (a decimal's scale does not matter),
so a result moved to another frame or session, or a snapshot edited after the plan was issued, is
refused. `producedUtc` is a UTC timestamp ending in `Z` that may not be after the validation or
score time. `source` names the `modelId` (opaque), `modelVersion` (bounded), and `kind`
(`modelled-estimate`, `deterministic-rule`, or `derived-calculation`) of what produced the result;
none of them is a live detection. The lock binds the plan to the private graph; the traceability
binds each result to one frame of that plan.

The scorer requires the still-eligible private manifest, the run plan recomputed from that exact
manifest, its truth commitment, the frozen threshold policy, and predictions bound to that plan's
lock.
It accepts predictions only for the plan's deterministic `Test` split and rechecks consent,
retention, revocation, privacy review, and observed pixel hashes at the UTC score time; it has no
fallback that fabricates an all-Test plan from raw samples.

The scorer never pools `real-raster`, `synthetic-raster`, and `post-ocr-evidence`. One aggregate
document has one evidence class and exactly one slice for every intent — Loot decision, Full
stash, Ammo, Keys, Quest/future-quest items, Map/extracts/timers, Health/character, and
Auto-detect. Each slice includes the original numerator/denominator, excluded unknown truth
scopes, excluded predictions in those unadjudicable scopes, independent split units, attempted eligible
truth coverage, accuracy, recall, false-positive rate, F1, each applicable metric numerator and
denominator, a 95% Wilson recall interval, abstention, confident-wrong, sequence completeness,
reordered-frame and overlap/dedup errors, and available performance. A repeated truth may match
once from any frame where it remains visible.

Sequence completeness is recomputable from two counts. `expectedFrames` is every planned frame in
the slice and `observedFrames` every one with at least one result of any status, unavailable
included; `missingFrames` is their difference and `sequenceCompleteness` is
`observedFrames / expectedFrames` (0 when nothing is expected). `reorderedFrames` counts frames, as
its name says: within each sequence, the observed frames in the order the producer answered them,
minus the longest run already in frame order, which is the fewest frames that would have to move.
The earlier count added one per disordered sequence, so a single swap and a full reversal of three
frames reported the same. Publication rejects an aggregate whose completeness counts disagree:
observed above expected, missing not equal to the difference, more split units than frames, frames
without units or without any truth (or the reverse), observed frames without results, or more
reordered frames than observed frames minus one. Every unsupported or underpowered slice is
`insufficient-data`; powered slices are explicitly `pass` or `fail` against the frozen candidate
coverage, abstention, confident-wrong, and F1 thresholds.

An unmatched claim in a sample/type scope whose truth is `unknown` is excluded rather than called
a false positive, and for the same reason it is never evidence of a confident-wrong answer: it may
be a claim about the unlabelled object. Confident-wrong is decided only after every known truth
has had its chance to match, so a claim that correctly answered one truth is not also the wrong
answer for another. A known truth is confident-wrong when it went unmatched and a remaining claim
of its kind, outside every unknown scope, has confidence at least 0.9 (the frozen
`FrozenConfidentWrongMinimumConfidence`). A correct claim plus an extra wrong claim records one TP
and one FP, not a confident miss.

For this claim-set benchmark, coverage is attempted eligible truth divided by eligible known truth;
an explicit detected or abstained result of the matching type is an attempt, while missing or
unavailable output is not. Accuracy is `TP / (TP + FP + FN)`, recall is `TP / (TP + FN)`, the
false-positive rate is `FP / (TP + FP)` because the corpus has no meaningful true-negative claim
universe, and F1 is `2TP / (2TP + FP + FN)`. Each denominator is emitted beside its numerator.

The candidate values in `thresholds/recognition-release.v1.json` are frozen before tuning. They
are deliberately candidate policy, not an accuracy claim. An aggregate may be published only when
every non-empty slice has at least five independent split units. Publication parses the typed
contract, rejects per-sample/private material even inside extensions, recomputes every arithmetic
identity, rate, Wilson interval, and threshold status, requires all eight unique slices, and
verifies that every slice matches the root evidence class. The `safeToPublish` and
`containsPerSampleResults` fields are assertions to check, never authority.

## Residual limitations

Two different kinds of limit remain, and they are not the same problem.

**Authenticity.** Nothing in the corpus workflow is signed. The plan lock is an unkeyed SHA-256:
it proves that a plan is consistent with a private manifest, and anyone holding that manifest can
recompute it, so it is a binding, not a credential. Prediction traceability proves that a result
answers a specific frame of a specific plan with the plan's own snapshot; `producedUtc` and
`source` are what the producer declares, bounded only by the score time and by shape, and nothing
proves the named model produced the result or when. A consent hash correlates a sample with its
private record and is not evidence that a person consented; that remains the human review's job.
`validate-aggregate` and `publish-aggregate` recompute every identity, rate, interval, status, and
privacy rule from the counts, so a tampered aggregate must at least be internally consistent, but a
fabricated and consistent aggregate still passes them: an aggregate is authentic only because the
operator produced it with `score-and-publish` against private material.

**Time of check to time of use.** Path canonicalization, the repository and Git-storage checks, and
the later read or write are separate system calls. A local process able to replace a path component
between the check and the open could redirect a read or a write, and the atomic output rename
replaces whatever is at the destination name at rename time. The tool reads the canonical path it
checked and writes through a new `CreateNew` temporary, which narrows the window without closing it;
closing it would need handle-relative opens that .NET does not expose portably. An attacker with
that access to the private corpus directory is out of scope for this single-user tool.

## Current baseline

`fixtures/recognition-corpus/baselines/current-main.v1.json` is intentionally exactly
`not-measured: blocked-no-consented-pixels`. Synthetic rendered scenes and post-OCR fixtures are
useful contract coverage but cannot change that status or establish real-raster accuracy. #301
must obtain actual consented captures, complete human privacy review, preserve the untouched test
split, and run the frozen scorer before a measured baseline can exist.

## Local use and validation

`tools/RecognitionCorpus` is a small standalone .NET tool. Private inputs and the emitted run plan
must be outside every repository/worktree:

```text
dotnet run --project tools/RecognitionCorpus -- validate-manifest /private/corpus/manifest.json
dotnet run --project tools/RecognitionCorpus -- emit-run-plan /private/corpus/manifest.json <run-id> <producer-id> <producer-version> /private/corpus/run-plan.json
dotnet run --project tools/RecognitionCorpus -- validate-run-plan /private/corpus/run-plan.json /private/corpus/manifest.json
dotnet run --project tools/RecognitionCorpus -- validate-predictions /private/corpus/predictions.json /private/corpus/run-plan.json
dotnet run --project tools/RecognitionCorpus -- score-and-publish /private/corpus/manifest.json /private/corpus/run-plan.json /private/corpus/predictions.json fixtures/recognition-corpus/thresholds/recognition-release.v1.json real-raster /review/aggregate-results.json
dotnet run --project tools/RecognitionCorpus -- validate-aggregate /review/aggregate-results.json fixtures/recognition-corpus/thresholds/recognition-release.v1.json
dotnet run --project tools/RecognitionCorpus -- publish-aggregate /review/aggregate-results.json fixtures/recognition-corpus/thresholds/recognition-release.v1.json ./aggregate-results.v1.json
```

`emit-run-plan` validates the private manifest at the current UTC time, plans only consented
material, serializes the plan, and then parses and validates those exact bytes against the same
manifest before writing them; an id or version the interchange rules reject never reaches disk.
Serialization is deterministic on every platform: fixed property order, LF line endings, no byte
order mark, and canonical decimals. The same manifest and ids therefore emit byte-identical plans
on Linux and Windows, and aggregates are written the same way. No command accepts a score or
eligibility time; tests inject a fixed clock through the library entry point only.

The second argument to run-plan validation is the authorized private planner context. The second
argument to prediction validation is the exact truth-free plan whose run, producer, lock,
membership, intent, evidence class, dimensions, and timing contract the output must match.
`score-and-publish` will not write an unsafe aggregate. `publish-aggregate` validates again and
writes only a canonical typed projection, dropping benign extension properties after hostile
privacy fields have failed.

Diagnostics never quote a filesystem path or document content that could hold one. A runtime I/O
exception names the file it failed on (a manifest locked by another process printed "The process
cannot access the file '.../manifest.json'"), so the CLI prints an exception's own text only when
the tool wrote it as fixed, path-free text, and otherwise one of fixed not-found, access-denied,
path-too-long, read/write, invalid-argument, or unexpected-failure messages. Document values enter
a message only when already shaped as an opaque id (otherwise `<non-opaque id>`) or a short
lowercase token (otherwise `<unrecognized value>`), property names only as short ASCII identifiers
(otherwise `<property>`), and malformed JSON is reported by line and byte, not by the runtime's
JSON path.

`fixtures/recognition-corpus/golden/` holds a wholly synthetic manifest and predictions with
invented labels, and the run plan and aggregate they must produce. The golden tests pin every
split unit and assignment, the plan lock, the run-plan bytes, and the aggregate bytes, with the
split, lock, and plan bytes recomputed independently of the tool; the CLI tests reproduce the same
bytes through `emit-run-plan` and `score-and-publish`. Tests load the checked-in
`thresholds/recognition-release.v1.json` rather than a copy built in code.

The test project covers positive, negative, hostile, privacy, split/leakage, region-bound,
prediction-mismatch, plan-lock binding, prediction traceability mutation, consent-scoped planning,
schema-compatible unknown-property, positional nested-version, forbidden-name alias, metric,
sequence completeness and reordering, confident-wrong scope, aggregate-tamper, threshold,
symlink/junction and two-hop parent-link escape, Git-storage, relative-traversal, original-name,
golden byte, CLI, and stderr path-omission cases. Both projects are registered in the solution so the normal Linux and Windows
jobs execute them; GitHub Actions stays the required substantive-change gate.
