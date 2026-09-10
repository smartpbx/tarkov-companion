# Product behavior

## Profile intelligence

The local profile is the default and sufficient source of player progress. It stores game mode, level, trader levels, completed tasks, objective counts, hideout station levels, wishlist items, owned counts, event states, and explicit item overrides. The JSON profile service writes an atomic, versioned document under a caller-selected local path. Import accepts unknown JSON fields for forward compatibility, validates required fields and bounded counts, rejects unsupported schemas, and persists the imported profile. Export serializes only the profile contract; it cannot carry API tokens or other arbitrary secret fields.

TarkovTracker remains an optional read-only integration. On Windows it is available in a disconnected state by default when per-user protected storage exists, with `TARKOV_COMPANION_TARKOVTRACKER_ENABLED=false` as an explicit opt-out; offline mode or unavailable protected storage disables Connect and Refresh. Availability checks do not contact the service. Only an explicit Connect validates a mode-scoped `GP` token, and only an explicit manual Refresh (or a rate-gated foreground refresh requested by a future caller) reads progress. Quest, hideout, wishlist, and recommendation context calculation does not depend on it and never writes to it.

The adapter calls only canonical HTTPS `GET /token` and `GET /progress`, rejects redirects, uses conditional ETags, honors quota and `Retry-After` backoff, and maps only supported task/objective state and count fields. It does not call team or mutation endpoints, import account/profile metadata or hideout state, schedule startup/background polling, or upload anything. A fetched snapshot enters the same preview, explicit conflict confirmation, atomic journal, and undo boundary as project JSON; unknown or invalid IDs remain unresolved. The UI labels fetch time as fetch time because the service supplies no per-field edit time or compatible local generation. Tokens stay in per-user Windows protected storage and are never shown again, logged, exported, or placed in fixtures.

Quest progress has a separate project-owned JSON v2 exchange surface. It exports exact local profile identity, mode, generation, task/objective assertions, explicit FIR-class holdings, and pins to a checksummed local file without raw assertion sources, authorization data, local paths, or external account IDs. Import is preview-only until the user reviews monotonic changes, resolves every conflict, and confirms apply; unknown catalog IDs remain unresolved evidence and missing records never delete local progress. Apply and undo are atomic, journaled, revision-bound, idempotent, and fully offline. The older profile settings schema 1 is handled only by an explicit compatibility reader: it binds to the deterministic legacy generation and proposes its explicit completed-task IDs, objective counts, and non-FIR owned counts, without inventing assertions for absent entities or confusing the settings envelope with v2.

Quest requirements are removed when their task is complete and reduced by recorded objective progress. Hideout requirements are removed when the target station level is built and reduced by locally owned item counts. The context service combines those results with wishlist membership, event state, user override, and specialized item advice for the existing recommendation engine.

Recommendation actions use this deterministic precedence:

1. explicit user override;
2. known allergic event state;
3. found-in-raid quest need;
4. other quest need;
5. hideout need;
6. wishlist membership;
7. untested event item;
8. specialized ammo or key advice;
9. flea/trader and value-per-slot economics.

Specialized advice is explanatory and does not silently defeat a stronger personal need or an explicit override.

## Event intelligence

Events are configured as generic definitions with applicable canonical item IDs. Applicable items begin `Untested`, then become `Safe` or `Allergic` only after the user records a result. Counts expose each state explicitly; the frozen `EventProgress.Unknown` field represents all not-yet-tested or unclassified applicable items.

Consumption result precedence is `Allergic` over `Safe` over `Untested`. Once any observation is allergic, a later safe observation cannot erase that warning. This is local recorded state, never a prediction or live detection.

## Ammunition intelligence

Ammo packs resolve to their contained canonical round before evaluation. Each caliber is ranked deterministically by penetration, then damage, then canonical item ID. The first round is S tier and the remaining ranks fall into A through D percentile bands. With a profile supplied, caliber lists exclude rounds blocked by player level, game mode, trader level, or task unlock rules; direct lookup still returns the round with `ObtainableForProfile` set appropriately.

Armor-class ratings are deliberately labeled as a heuristic, not a live detection or exact combat simulation. The calculation compares round penetration against `armor class × 10`, with fixed margins for Poor, Limited, Fair, Good, and Excellent. Learn Mode always includes that rule and the underlying penetration/damage values. Confidence is capped at 0.80 because the result is heuristic, even when source data has higher confidence.

## Key intelligence

Key scoring is explicit and inspectable. The weighted Core score combines:

- personal incomplete quest relevance (30%);
- expected-loot economics relative to acquisition cost (15%);
- finite or reusable uses (10%);
- lock breadth plus configured utility (20%);
- unique access (15%);
- loot adjusted for route risk (10%).

Generated explanations include personal incomplete-task count, lock count, loot, cost, utility, unique access, and risk. Curated overrides may replace score, tier, advice, or explanation, but construction rejects an override without a non-empty source, dated provenance, reference, and confidence. The example asset is disabled and contains no asserted game fact; reviewed data must replace every placeholder before enabling it.

## Loadout intelligence

Loadout evaluation sums estimated cost and weight for every selected occurrence, including repeated magazines and medical items. It checks slot category, weapon/ammunition caliber, magazine/weapon and magazine/ammunition compatibility, and plate/armor compatibility. Missing catalog data produces a compatibility issue and makes weight unknown instead of presenting a partial number as complete.

Warnings cover profile-unobtainable ammunition, low-tier ammunition paired with a kit worth at least 150,000 roubles, and armor with no selected plate. These are planning heuristics based on cached/public facts, not input automation or live gameplay detection.

## Safety and provenance

All behavior is external to Escape from Tarkov. It uses local user state and public or curated data, with no memory access, injection, network interception, input generation, gameplay automation, enemy detection, or in-game overlay. Runtime structured data remains sourced from `json.tarkov.dev`; uncertain claims preserve source, timestamp, reference, and confidence.
