# Quest tracking — staged implementation plan

Status: Stages 1 through 4 implemented; optional external integration and final hardening remain.

Read root `AGENTS.md` and
`docs/research/QUEST_TRACKING_ARCHITECTURE.md` before changing code. The
research document is the proposed decision record until the coordinator accepts
it into `docs/adr/`; do not treat proposed interfaces or table names as frozen
contracts before that review.

## Product decision to implement

- Local progress is canonical and works offline.
- `json.tarkov.dev` is the runtime task/map catalog; the deprecated GraphQL
  service is development-time schema evidence only.
- Project JSON import/export is implemented before external integration.
- TarkovTracker is optional, read-only, GET-only, mode-scoped, and never
  required for quest, map, item-need, or recommendation behavior.
- External snapshots use preview, conflict resolution, provenance, journaling,
  and undo. Fetch time is not treated as source edit time.
- Quest map content is second-screen static catalog information. It never
  becomes a game overlay or live player/enemy/object detection.

## Recommended work split

The coordinator should assign explicit ownership before dispatch because this
feature crosses existing data, profile, map, Windows, and App areas.

1. **Catalog fidelity:** forward migration, expanded task/objective DTOs,
   prerequisites, failures, multiple maps, zones, and unsupported variants.
2. **Local progress domain:** profile mode/generation, commands, journal,
   eligibility, quest detail, and item-need read models.
3. **Quest/map UI:** quest board/detail and source-honest static objective
   layer using the approved map projection boundary.
4. **Owned exchange:** project JSON v2, compatibility import, preview,
   conflicts, transactional apply, and undo.
5. **Optional external adapter:** secure TarkovTracker connect/disconnect and
   supported `GET /token` and `GET /progress` only.
6. **Independent review:** full-size fixture/schema drift, offline/recovery,
   privacy, safety, license, accessibility, and composed-runtime verification.

Do not rewrite `0001_initial.sql`; add a numbered forward migration. Coordinate
Core contract changes through the integration owner, and keep Windows secret
storage behind a narrow interface. Every substantive stage requires deterministic
tests and must run `scripts/build.sh` and `scripts/test.sh` before commit.

## Stage gates

- The catalog stage must retain raw source JSON and import all current
  objective kinds as normalized or explicitly unsupported.
- The local stage must survive restart/offline and isolate PvP, PvE, and
  seasonal generations.
- The map stage must suppress exact markers when geometry or transform is not
  validated and retain source/attribution/timestamp.
- The exchange stage must be atomic, idempotent, secret-free, wrong-mode safe,
  previewed, journaled, and reversible.
- The TarkovTracker stage must prove canonical-host HTTPS, redirect rejection,
  token redaction, quota/backoff behavior, no team access, and no HTTP mutation.
- Release claims must describe only behavior that is actually composed into the
  running app.
