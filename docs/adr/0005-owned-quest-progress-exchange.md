# ADR 0005: Owned quest progress exchange and reconciliation

Status: Accepted — 2026-09-10

## Context

ADR 0004 makes local, mode-and-generation-scoped quest progress canonical and selects the project's own JSON format as the first interchange surface. A backup file is untrusted input: it may be oversized, deeply nested, malformed, stale, copied from another wipe or mode, or edited after preview. Applying records one at a time would also allow state and audit history to diverge on a mid-import failure.

The existing profile JSON is a settings envelope. Its schema version is not the quest progress interchange version, even though profile schema 1 contains older quest-shaped convenience fields. Treating those fields as complete quest assertions would invent state that schema 1 could not express.

## Decision

The owned format identifier is `tarkov-companion.quest-progress`, independently versioned at format version 2. The envelope carries exporter application version, UTC export time, a fixed non-secret provenance summary, a canonical payload, and a SHA-256 checksum of that normalized payload. Each profile contains its exact local UUID, display name, `GameMode`, profile generation, task states, objective states with nullable decimal counts, explicit FIR/non-FIR holdings, and task/objective pins. Arrays are emitted in ordinal deterministic order. Assertion-source strings, record timestamps, local paths, raw authorization data, secrets, and external account identifiers are not part of the schema. Sensitive-looking optional pin notes are omitted; sensitive-looking required identity fields cause export to fail rather than produce an unsafe file.

JSON import is bounded before reconciliation: 2 MiB maximum file size, depth 32, eight profiles, 50,000 owned records, 4,096 characters per general string, 256 characters per source ID/profile name, and 128 characters per generation. Required fields and exact supported enums are validated, owned entity keys must be unique in each profile, and counts must be nullable finite nonnegative decimals or nonnegative 32-bit holding integers. Unknown JSON fields are tolerated and excluded from the normalized checksum. Unsupported format identifiers or versions fail without database work.

The explicit legacy reader recognizes only profile settings schema 1 and binds it to the same deterministic `legacy-{profileId:N}` generation used by profile migration. It preserves only assertions that schema made explicitly: `completedTaskIds` become `Completed` task proposals, `objectiveProgress` entries become `InProgress` proposals with their stated counts, and `ownedItemCounts` become explicit non-FIR holding proposals. It creates no rows for absent entities, still requires the exact active profile ID, name, mode, and deterministic generation, and does not accept the profile settings schema 2 envelope as quest progress.

Application reconciliation selects one exact active profile ID, name, mode, and generation. It compares only records present in the imported profile; absence is never deletion. Known task/objective promotions, count increases, new or increased exact-class holdings, and new pins are safe monotonic proposals. Regressions, completed-versus-failed state, lower/removed counts, and changed existing pins are conflicts requiring an explicit `KeepLocal` or `UseIncoming` decision. IDs absent from the selected mode catalog are retained as unresolved import evidence and never applied. Exact matches are ignored.

Apply is bound to the preview SHA-256 and base progress revision. The SQLite adapter revalidates the preview hash, exact conflict-resolution set, and current revision inside one transaction. State rows, the profile revision, import metadata, conflict decisions, unresolved records, and append-only journal entries containing inverse values commit together. A unique exact-scope normalized payload hash makes retries idempotent. A journal insert or any other failure rolls the whole transaction back.

Undo loads the applied import's inverse journal and writes the restoration as a new import-actor journal batch. It never updates or deletes prior journal entries. Undo is idempotent and is refused if later progress revisions would be overwritten.

Infrastructure performs export with a temporary file in the destination directory, write-through flush, durable file flush, and same-filesystem replace. The Avalonia quest view exposes a local file path, export, preview categories, explicit batch conflict choices, apply, and undo of the last session import. None of these operations performs network access.

## Consequences

Owned quest fields round-trip deterministically when they contain export-safe text. Malformed, stale, tampered, wrong-profile, wrong-mode, and wrong-generation plans cannot partially mutate progress. Import history remains explainable and reversible, and unknown catalog IDs remain visible evidence rather than disappearing or contaminating another scope.

The format is intentionally not a general settings backup; legacy schema 1 support is a bounded compatibility adapter for its three explicit owned-progress collections, not a reinterpretation of all settings. The app does not import third-party backup formats. TarkovTracker support, external accounts, uploads, telemetry, automation, game access, and external progress writes remain deferred and require later decisions.
