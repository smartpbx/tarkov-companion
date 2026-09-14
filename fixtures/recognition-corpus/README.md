# Recognition corpus fixtures

This directory contains only versioned policy, schemas, synthetic contract examples, and
privacy-safe aggregate status. It deliberately contains no pixels, filenames, absolute paths,
private manifests, consent records, OCR strings, per-sample truth, or per-sample predictions.

The corpus itself lives outside every repository and worktree. `RecognitionCorpus` refuses an
ingest path below a Git worktree and strips/forbids filesystem names before it validates a
manifest. Real raster material remains ineligible until its independently observed decoded-pixel
hash, full private consent record, full privacy review, permitted uses, UTC
consent/review/retention times, and active revocation state all validate together. Hash-shaped
summaries without those records do not authorize a sample.

`schemas/` defines the v1 private manifest, content-locked run plan, bounded-region prediction,
and aggregate interchange. `thresholds/` freezes the scorer policy before recognizer tuning.
`baselines/` is intentionally not measured until issue #301 supplies consented real raster
evidence. The synthetic plan example contains only a lock-shaped placeholder and cannot validate
without an authorized private manifest.
