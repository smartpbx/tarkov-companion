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
| What did the wave-2 serialization and external-data review find? | [audits/SERIALIZATION_AND_EXTERNAL_DATA.md](audits/SERIALIZATION_AND_EXTERNAL_DATA.md) |
| What did the wave-2 cryptography, secrets, transport, and update review find? | [audits/CRYPTO_SECRETS_AND_UPDATES.md](audits/CRYPTO_SECRETS_AND_UPDATES.md) |
| What did the wave-2 diagnostics, error, and privacy review find? | [audits/DIAGNOSTICS_ERRORS_AND_PRIVACY.md](audits/DIAGNOSTICS_ERRORS_AND_PRIVACY.md) |
| What did the wave-2 persistence and local-state review find? | [audits/PERSISTENCE_AND_LOCAL_STATE.md](audits/PERSISTENCE_AND_LOCAL_STATE.md) |
| What did the wave-2 Windows platform-boundary review find? | [audits/WINDOWS_PLATFORM_BOUNDARIES.md](audits/WINDOWS_PLATFORM_BOUNDARIES.md) |
| What did the wave-2 relay authorization and state-machine review find? | [audits/RELAY_AUTHORIZATION_AND_STATE.md](audits/RELAY_AUTHORIZATION_AND_STATE.md) |
| What is future or being rebuilt, and what threat modeling does it still owe? | [TBD_COMPONENTS.md](TBD_COMPONENTS.md) |
| Exactly what shipped in this pass, and what remains before #317 can close? | [PHASE_STATUS.md](PHASE_STATUS.md) |

Illustrative fixtures for the abuse cases live in `tests/security/` (owned alongside this
directory) — see `tests/security/README.md` for what they are and, as importantly, what they are
not.

The six files under `audits/` are source-review evidence lanes, not parallel risk registers and
not proof of mitigation. Their lane IDs are reconciled into the stable abuse-case and risk IDs in
`ABUSE_CASES.md` and `CONTROLS_AND_RESIDUAL_RISK.md`; `PHASE_STATUS.md` contains the complete
crosswalk and reproducible counts. The later issue-#278 authorization core is recorded separately
as tested but uncomposed evidence so it cannot be mistaken for deployed protection. Issue #317
remains open until implementation, exact-head CI, and any required manual `dev` verification
satisfy its release criteria.

## Product presentation contract

This directory is the canonical, complete account of the product's safety boundaries, review
method, trust model, controls, and accepted or deferred risks. The v2 Setup/Admin surface must
provide a clearly labelled route to this material (a bundled view or a link to the matching
version of these documents) so a player or operator can inspect the full reasoning without
reading the repository.

Routine desktop and tablet workflows should use progressive disclosure: a concise statement of
what is happening, with "why", source, and technical detail available on demand. Concision must
never hide active consent, outbound-data scope, pairing/authentication plus effective transport,
an active security/failure state, a destructive consequence, or decision-changing uncertainty.
The complete accepted/deferred risk register must be clearly reachable, version-matched under
Setup/Admin Data & Privacy; it need not be repeated in every routine workflow. Historical/modelled
guidance keeps its category identity inline, plus freshness and confidence when either could change
a decision. Full source, observed/data-through/generated timestamps, coverage, calibration, and
model version remain available on demand in Why/details and Setup/Admin. This PR records that
product requirement; implementing the Setup/Admin surface remains future work listed in
[TBD_COMPONENTS.md](TBD_COMPONENTS.md).

## Safety and evidence categories this document set reviews

These are inherited from `AGENTS.md`, `docs/SAFETY.md`, and `docs/V2_CONTRACT.md`, not decided
here. Memory access, generated game input, and an in-game overlay are the three immutable fixtures.
The other implementation restrictions are current enforced design exclusions, and the evidence
rule is the normative V2 contract. This review may identify a gap but may not silently relax any
category:

1. Never read or write Escape from Tarkov process memory. **Immutable.**
2. Never inject code or DLLs, and never hook the game renderer.
3. Never inspect, intercept, or decode Escape from Tarkov network traffic.
4. Never generate game-directed mouse, keyboard, or controller input. **Immutable.**
5. Never automate flea purchases, sales, inventory actions, aiming, or combat.
6. Never implement enemy detection, ESP, radar, or live player tracking.
7. Never render an in-game overlay. **Immutable.**
8. Historical or modelled intelligence must carry source, observed/data-through/generated UTC
   timestamps, coverage/sample size, confidence, and model version, and must never be presented
   as live.

[ANTI_CHEAT_REVIEW.md](ANTI_CHEAT_REVIEW.md) is the review of the current, real architecture
against this list — not a restatement of the list itself.

## Scope of this pass

Everything documented here was produced by reading the repository as it exists on `main` at the
time of writing (commit `76b506f` and earlier) — `AGENTS.md`, `README.md`, `docs/SAFETY.md`,
`docs/ARCHITECTURE.md`, `docs/GROUP_RELAY.md`, `docs/OPERATIONS.md`, the ADRs in `docs/adr/`, and
the current source for GroupServer, group-session/settings storage, screenshot/GDI capture and
recognition/retention, profile and quest import/export, raid-history export, DPAPI, TarkovTracker,
Velopack/relay updating, and the CI workflows that gate safety and secrets. No component's
behavior is asserted from an issue description alone where source
was available; disagreements between source and normative policy are recorded as open findings
rather than resolved by rewriting the policy in this worktree.

Components described in open v2 issues (#304, #305, #306, #310, #311, and others) that are absent
or scheduled for a material rebuild are listed in [TBD_COMPONENTS.md](TBD_COMPONENTS.md) rather
than modeled as if their future design existed. The source-grounded V1 baseline was commit
`76b506f`; this revision is reconciled with the now-merged #264 `docs/V2_CONTRACT.md`, Core
evidence/wire types, validation guards, and deterministic contract tests. Future #305/#311 feature
implementations still require their own threat review and presentation assertions.
