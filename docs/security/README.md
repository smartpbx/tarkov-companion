# Security documentation

The threat model for Tarkov Companion, started as the phase-1 foundation of
[#317](https://github.com/smartpbx/tarkov-companion/issues/317). It is a baseline, not a
closeout: see [PHASE_STATUS.md](PHASE_STATUS.md) for exactly what this pass covered and what it
did not.

## Reading order

| Question | File |
| --- | --- |
| How was this produced, and what do the words in it mean? | [METHODOLOGY.md](METHODOLOGY.md) |
| What is the system, and where does trust change hands? | [SYSTEM_AND_TRUST_BOUNDARIES.md](SYSTEM_AND_TRUST_BOUNDARIES.md) |
| What is worth protecting, and who might attack it? | [ASSETS_AND_ACTORS.md](ASSETS_AND_ACTORS.md) |
| What could go wrong at each boundary? | [ABUSE_CASES.md](ABUSE_CASES.md) |
| What stops each abuse case, and what is left over if it fails? | [CONTROLS_AND_RESIDUAL_RISK.md](CONTROLS_AND_RESIDUAL_RISK.md) |
| Does the architecture actually keep the anti-cheat promises in `docs/SAFETY.md`? | [ANTI_CHEAT_REVIEW.md](ANTI_CHEAT_REVIEW.md) |
| What hasn't been built yet, and what threat modeling does it still owe? | [TBD_COMPONENTS.md](TBD_COMPONENTS.md) |
| Exactly what shipped in this pass, and what remains before #317 can close? | [PHASE_STATUS.md](PHASE_STATUS.md) |

Illustrative fixtures for the abuse cases live in `tests/security/` (owned alongside this
directory) — see `tests/security/README.md` for what they are and, as importantly, what they are
not.

## Non-negotiable boundaries this document set assumes

These are inherited from `AGENTS.md` and `docs/SAFETY.md`, not decided here, and no finding in
this document set may propose relaxing them:

1. Never read or write Escape from Tarkov process memory.
2. Never inject code or DLLs, and never hook the game renderer.
3. Never inspect, intercept, or decode Escape from Tarkov network traffic.
4. Never generate game-directed mouse, keyboard, or controller input.
5. Never automate flea purchases, sales, inventory actions, aiming, or combat.
6. Never implement enemy detection, ESP, radar, or live player tracking.
7. Never render an in-game overlay.
8. Historical or modelled intelligence must carry source, observed/data-through/generated UTC
   timestamps, coverage/sample size, confidence, and model version, and must never be presented
   as live.

[ANTI_CHEAT_REVIEW.md](ANTI_CHEAT_REVIEW.md) is the review of the current, real architecture
against this list — not a restatement of the list itself.

## Scope of this pass

Everything documented here was produced by reading the repository as it exists on `main` at the
time of writing (commit `76b506f` and earlier) — `AGENTS.md`, `README.md`, `docs/SAFETY.md`,
`docs/ARCHITECTURE.md`, `docs/GROUP_RELAY.md`, `docs/OPERATIONS.md`, the ADRs in `docs/adr/`, and
the source of `TarkovCompanion.GroupServer`, the DPAPI secret store, the TarkovTracker client,
and the CI workflows that gate safety and secrets. No component's behavior is asserted from an
issue description alone where the source was available to check against it; where the source
disagreed with an issue's plan, the source is what is documented.

Components described in open v2 issues (#304, #305, #306, #310, #311, and others) that have no
implementation in `src/` yet are listed in [TBD_COMPONENTS.md](TBD_COMPONENTS.md) rather than
modeled as if they existed. `docs/V2_CONTRACT.md`, owned by #264, had not landed on `main` at the
time of writing; this document set treats the evidence/provenance rules in `docs/SAFETY.md` as
the current normative source and will need reconciling against the v2 contract once it merges.
