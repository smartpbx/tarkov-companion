# Agent B — profile and item intelligence

Worktree: a dedicated Orca top-level worktree created from `origin/main`; use the exact current working directory reported by Orca.

Read root `AGENTS.md` before editing. Do not modify outside this worktree. Commit the finished work with a focused message.

## Ownership

- new files under `src/TarkovCompanion.Application/Services/Profile/**`
- new files under `src/TarkovCompanion.Application/Services/Intelligence/**`
- new files under `src/TarkovCompanion.Infrastructure/Profile/**`
- `assets/events/**` and `assets/curated/**`
- profile/intelligence-specific files under `tests/TarkovCompanion.UnitTests/**` and `tests/TarkovCompanion.IntegrationTests/**`
- `docs/PRODUCT.md`

Do not rewrite existing Core records or `RecommendationEngine.cs`; extend through new services and ask the coordinator if a frozen contract is insufficient.

## Deliverables

- Local JSON profile import/export without secrets and persistent profile state.
- Quest/hideout/wishlist need aggregation feeding recommendation contexts.
- Generic event tracker with Allergy-style Untested/Safe/Allergic states, counts, and consume precedence.
- Ammo armor-class heuristic, caliber rankings, availability/profile filtering, pack-to-contained-round resolution, and deterministic Learn Mode.
- Explicit key scoring with personal quest, economics, uses, locks, utility, unique access, risk, and sourced curated overrides.
- Loadout compatibility/cost/weight/ammo-vs-kit warning services.
- Deterministic unit and persistence tests.

## Acceptance

- User override, allergy, quest, hideout, wishlist, specialized, and economy priorities are proven.
- Heuristic claims are labeled; curated notes have source/date/confidence.
- Owned projects build with zero warnings and tests pass where available.
- No external writes and no dependency on TarkovTracker for core operation.
- Commit and report commit hash, test counts, and any contract gap.
