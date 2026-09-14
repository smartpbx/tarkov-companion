# Review methodology

## What kind of review this is

This baseline is a **source-grounded architectural review**: every claim about how the system
behaves was checked against the actual code, config, or CI workflow that implements it, not
against what an issue or a doc said it should do. Where those disagreed, this document set
follows the code and flags the doc as stale.

It is not, and does not claim to be, any of the following:

- **A penetration test.** Nobody sent traffic at a running relay or a running desktop build as
  part of this pass. Every abuse case is a reasoned scenario grounded in the real request
  handlers, not an observed exploit.
- **A fuzzing or static-analysis run.** No tool was executed against the codebase for this pass
  beyond the CI checks that already run automatically (`audit-safety.sh`, `scan-secrets.sh`) and
  are cited as such.
- **A completed adversarial review of #317's full acceptance criteria.** #317 asks for review of
  serialization, cryptography, state machines, external data sources, secret handling,
  authorization, error handling, and platform boundaries, each with verification evidence and an
  acceptance decision. This pass produces the trust-boundary map, asset/actor taxonomy, abuse
  cases, and a first residual-risk register that later passes verify against; it does not close
  every one of those bullets. See [PHASE_STATUS.md](PHASE_STATUS.md).

## Evidence tiers — say which one, every time

Every claim in this document set about a control uses one of these words, and the words are not
interchangeable:

| Tier | Means | How to write it |
| --- | --- | --- |
| **Implemented** | The code that enforces this exists and was read during this review. | "reads `X`, which does `Y`" |
| **Reviewed** | A person (or agent, in this pass) read the implementation and reasoned about whether it holds, without running it. | "reviewed by reading `path/File.cs`" |
| **Tested (automated)** | An automated test exercises this and runs somewhere — locally, in CI, or both — and the run was observed to pass. | "covered by `TestName` in CI" |
| **Tested (manual)** | A person ran the system and observed the behavior directly. | "verified manually on `dev` on `<date>`" |
| **Planned** | The control does not exist yet; it is proposed or tracked by an issue. | "planned — see #NNN" |

A finding may never claim "tested" when only "reviewed" or "planned" is true. Every entry in
[CONTROLS_AND_RESIDUAL_RISK.md](CONTROLS_AND_RESIDUAL_RISK.md) states its evidence tier
explicitly, and this pass introduces no entry above **Reviewed** except for the two controls that
already run in `ci.yml` / `windows-verify.yml` (`audit-safety.sh`, `scan-secrets.sh`), which are
cited as **Tested (automated)** because their CI runs are observable in the workflow files
themselves, not because this pass executed them.

## Severity taxonomy

Aligned to #317's acceptance criteria (critical/high/medium/low), applied per finding:

- **Critical** — breaks an immutable boundary in `docs/SAFETY.md` (memory access, injection,
  traffic decoding, input synthesis, live tracking, overlay), or fully compromises a secret that
  gates access to another user's data (group key derivation, admin key, DPAPI-protected token)
  with no attacker precondition beyond network reach.
- **High** — a realistic actor gets unauthorized read/write of another player's or another
  group's data, or can impersonate a party member, without needing a secret they shouldn't have.
- **Medium** — degrades availability or integrity for a bounded blast radius (one room, one
  report queue) but does not cross a trust boundary into data the actor shouldn't see or control.
- **Low** — requires an already-privileged position (holding the admin key, running as the same
  Windows user, controlling the update channel) or has effect bounded by an existing structural
  control (a byte cap, a count cap, a TTL).

**No unresolved high or critical finding may be silently accepted.** Every High/Critical entry in
the residual-risk register carries an explicit acceptance decision (accept / mitigate / defer)
with a named owner and, for accept, a stated reason. A High/Critical finding with no acceptance
decision recorded is a gap in this baseline, not a closed item — see
[PHASE_STATUS.md](PHASE_STATUS.md) for the ones still open.

## Abuse-case structure

Each entry in [ABUSE_CASES.md](ABUSE_CASES.md) follows the same shape, matching #317's request
for concrete failure scenarios:

- **Boundary** — which trust boundary in
  [SYSTEM_AND_TRUST_BOUNDARIES.md](SYSTEM_AND_TRUST_BOUNDARIES.md) this crosses.
- **STRIDE category** — Spoofing, Tampering, Repudiation, Information disclosure, Denial of
  service, or Elevation of privilege.
- **Actor** — from [ASSETS_AND_ACTORS.md](ASSETS_AND_ACTORS.md).
- **Scenario** — concrete inputs or sequence, not a general description ("a client POSTs
  `/waypoints` 61 times to the same room in one call", not "an attacker floods waypoints").
- **Cross-reference** — the ID of the matching row in
  [CONTROLS_AND_RESIDUAL_RISK.md](CONTROLS_AND_RESIDUAL_RISK.md).

A handful of the abuse cases have a matching illustrative fixture in `tests/security/fixtures/`.
Those fixtures are data files, not runnable tests — `tests/security/README.md` says why and what
would need to change to wire them into `TarkovCompanion.IntegrationTests`.

## Anti-cheat review method

[ANTI_CHEAT_REVIEW.md](ANTI_CHEAT_REVIEW.md) checks each immutable boundary against three
independent things, and says which of the three actually holds for that boundary today:

1. **The pattern-level static control** — `scripts/audit-safety.sh`, which greps `src/` and the
   `.props` files for named forbidden APIs and packages (`OpenProcess`, `SendInput`,
   `SharpPcap`, …) and runs in `ci.yml` and `windows-verify.yml` on every push. This catches a
   literal reintroduction of a forbidden call; it does not catch a boundary violated through a
   pattern the list doesn't name.
2. **The architectural boundary** — whether the layering in `docs/ARCHITECTURE.md` (Core has no
   platform dependency; Windows P/Invoke lives only in `Platform.Windows`; the strategy engine
   consumes only static/public inputs) makes the violation structurally awkward to write, not
   just currently absent.
3. **What was actually read** — for each boundary, which files this pass read to confirm the
   absence, so a future reviewer can tell a verified absence from an unchecked one.

## What "TBD" means here

An entry marked TBD is not a placeholder for effort not yet spent — it names a component that has
no implementation in `src/` as of this pass (verified by directory listing, not by assumption).
Modeling a nonexistent component's data flow would be inventing detail this review cannot back
with source, which is exactly the kind of unearned confidence #317 asks this review to avoid.
[TBD_COMPONENTS.md](TBD_COMPONENTS.md) lists what each one will owe once it exists, so the debt is
visible rather than silently dropped.
