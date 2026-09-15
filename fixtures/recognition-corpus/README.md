# Recognition corpus fixtures

This directory contains only versioned policy, schemas, synthetic contract examples, hostile
parser fixtures, synthetic golden fixtures, and privacy-safe aggregate status. It deliberately
contains no pixels, real capture names, absolute paths, private manifests, consent or privacy
records, OCR strings, or real per-sample truth or predictions. The invented traversal/name strings
under `hostile/` exist only to prove that extension fields cannot smuggle those classes of data
through validation: a relative traversal, an original-name field, original-name aliases that differ
only by case or separators, a complete consent document declared at a position v1 never
defines, Windows root-relative and drive-relative paths, and file URIs and paths behind full-width
separators or a zero-width space.

`golden/` is the one place with per-sample labels, and every one is invented: a synthetic-raster
manifest with no consent or privacy records, predictions for it, and the run plan and aggregate
they must produce byte for byte. They pin the split, plan lock, and serializer output. Two decimals
in the manifest are deliberately spelled `1.250` and `0.50` to prove canonicalization. Every
prediction repeats its plan sample's provenance and lineage with a produced time and model source,
and the fifth ammo frame deliberately has no result, so the ammo slice reports four of five frames
observed. Regenerate them only for a deliberate, versioned contract change.

The corpus itself lives outside every repository and worktree. `RecognitionCorpus` resolves
every private path one component at a time, including the parents inside each link target, and
refuses lexical or symlink/junction paths that enter a Git repository, worktree, or Git storage. It also forbids original-name fields, absolute paths, and relative
traversal strings at any nesting depth before it validates an interchange. Real raster material
remains ineligible until its independently observed decoded-pixel
hash, full private consent record, full privacy review, permitted uses, UTC
consent/review/retention times, and active revocation state all validate together. Hash-shaped
summaries without those records do not authorize a sample.

`schemas/` defines the v1 private manifest, truth-committed run plan, bounded-region prediction,
and aggregate interchange. `thresholds/` freezes the scorer policy before recognizer tuning.
`baselines/` is intentionally not measured until issue #301 supplies consented real raster
evidence. The synthetic plan example contains only a lock-shaped placeholder and cannot validate
without an authorized private manifest.
