# Agent C — recognition pipeline

Worktree: a dedicated Orca top-level worktree created from `origin/main`; use the exact current working directory reported by Orca.

Read root `AGENTS.md` before editing. Do not modify outside this worktree. Commit the finished work with a focused message.

## Ownership

- `src/TarkovCompanion.Infrastructure/Recognition/**`
- `fixtures/recognition/**` and `fixtures/screenshots/**`
- recognition-specific files under `tests/TarkovCompanion.RecognitionTests/**`
- `docs/RECOGNITION.md`

Do not edit Windows capture, UI, or existing Core contracts without coordinator approval. Do not add cloud OCR or screenshot upload.

## Deliverables

- Deterministic fixture OCR engine and provider-neutral OCR coordination.
- Resolution/scale-aware context detector for single item, container, extract list, flea listing, and unknown contexts.
- OCR normalization/confusion handling and fuzzy canonical item resolver.
- Lightweight Skia-based perceptual/icon fingerprint fallback.
- Confidence thresholds: auto-select at >= .90; explicit ambiguity at .70–.90; candidates below.
- Extract fuzzy matching against current-map canonical extracts.
- Functional container-beta segmentation/aggregation/drop-first logic with low-confidence exclusion.
- Visible flea row/price parser, informational only.
- Synthetic fixtures/tests representing 1080p, 1440p, 4K and 100/125/150 percent scale/noise.

## Acceptance

- Captures stay in memory and are not persisted by default.
- Unknown scenes never fabricate state.
- Recognition logic has no recommendation business rules.
- Owned project builds with zero warnings; tests pass where available.
- Commit and report commit hash, fixture accuracy, test counts, and limitations.
