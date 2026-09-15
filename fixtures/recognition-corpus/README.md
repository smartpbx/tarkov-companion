# Recognition corpus fixtures

This directory contains only versioned policy, schemas, synthetic contract examples, hostile
parser fixtures, and privacy-safe aggregate status. It deliberately contains no pixels, real
capture names, absolute paths, private manifests, consent records, OCR strings, per-sample truth,
or per-sample predictions. The invented traversal/name strings under `hostile/` exist only to
prove that extension fields cannot smuggle those classes of data through validation.

The corpus itself lives outside every repository and worktree. `RecognitionCorpus` resolves the
canonical target of every private input and refuses lexical or symlink/junction paths that enter
a Git repository/worktree. It also forbids original-name fields, absolute paths, and relative
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
