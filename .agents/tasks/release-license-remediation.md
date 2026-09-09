# Agent J — final dependency licensing and package notices

Work from current `origin/main` only after the tarkov.dev map work and production OCR dependency work have been integrated. Read `AGENTS.md`, `docs/LICENSING.md`, `docs/THIRD_PARTY_NOTICES.md`, `docs/reviews/SAFETY_LICENSE_REVIEW.md`, every source project file, and the resolved transitive package graph. Commit the result and report the hash and exact verification evidence.

## Goal

Make the Windows release archive license-complete for every managed/native/font/data component it actually ships, including the final map-rendering and OCR dependency graph.

## Deliverables

1. Generate a locked, transitive inventory for all distributed source projects at the final package versions. Separate build/test-only dependencies from shipped runtime dependencies.
2. Verify each component's license from authoritative package/repository metadata. Include managed wrappers and bundled native/runtime payloads separately where their licenses differ.
3. Rewrite `docs/THIRD_PARTY_NOTICES.md` with component, exact version, copyright/notice, license identifier, purpose, authoritative source, and whether it ships. Include at least the components identified by the independent safety/license review, plus `Svg.Skia`, the chosen OCR wrapper/native Tesseract/Leptonica payloads, traineddata provenance/license, and any new transitive packages.
4. Add a `LICENSES/` directory containing the required verbatim license/notice texts for shipped dependencies. Prefer the exact files distributed in the locked NuGet packages or authoritative upstream repositories; preserve copyright lines. Do not invent notices.
5. Update `scripts/audit-licenses.sh` to produce a deterministic machine-readable inventory and fail if a shipped component lacks a mapped notice/license file. Keep it network-independent after restore.
6. Update `scripts/package-windows.sh` and CI publishing so `LICENSES/`, final notices, and the inventory are included. Make packaging start from a clean staging directory and avoid stale zip entries.
7. Update `docs/LICENSING.md` and the release report with the actual final audit result. Do not claim third-party map artwork is bundled; runtime-cached CC BY-NC-SA assets must remain separate and attributed in-app.

## Acceptance

- License audit passes against the resolved final graph and fails under a test fixture with a missing mapping.
- Release archive contains complete notices, license texts, inventory, project license, and no third-party map artwork/cache.
- No dependency is marked permissive without evidence.
- Build/test/audit/package succeed with zero warnings.
