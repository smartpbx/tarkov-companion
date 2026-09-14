# Recognition corpus fixtures

This directory contains only versioned policy, schemas, synthetic contract examples, and
privacy-safe aggregate status. It deliberately contains no pixels, filenames, absolute paths,
private manifests, consent records, OCR strings, per-sample truth, or per-sample predictions.

The corpus itself lives outside every repository and worktree. `RecognitionCorpus` refuses an
ingest path below a Git worktree and strips/forbids filesystem names before it validates a
manifest. Real raster material remains ineligible until its private consent record, privacy
review, permitted uses, retention term, and revocation state all validate together.

`schemas/` defines the v1 producer interchange. `thresholds/` freezes the scorer policy before
recognizer tuning. `baselines/` is intentionally not measured until issue #301 supplies
consented real raster evidence.
