# Phase status

This tracks exactly what this pass of #317 delivered against #317's own acceptance criteria and
closeout checklist, so nobody has to re-derive it from reading every file. **This PR does not
close #317** — it lays the foundation the rest of #317's acceptance criteria build on.

## What this pass delivered

- `SYSTEM_AND_TRUST_BOUNDARIES.md` — a system/data-flow diagram and ten named trust boundaries
  (TB-1 through TB-10), each with a source citation, covering every component #317 names that
  actually exists in `src/` today: desktop application, group relay, tablet, update mechanism,
  data ingestion. Update mechanism and data ingestion are covered as part of TB-3/TB-5/TB-10 rather
  than as separate top-level boundaries, since the source did not support treating them as
  architecturally distinct trust changes from the boundaries they're folded into.
- `ASSETS_AND_ACTORS.md` — 13 classified assets, 10 actors, and a matrix tying actors to the
  boundaries where they're the relevant threat source.
- `ABUSE_CASES.md` — 14 STRIDE-categorized abuse cases across 6 boundaries, each with a concrete
  scenario and (for 7 of them) an illustrative fixture in `tests/security/fixtures/`.
- `CONTROLS_AND_RESIDUAL_RISK.md` — 10 open findings and 4 closed findings, every one with an
  evidence tier and, for open findings, an explicit acceptance decision — including one High
  finding recorded as **not yet decided** rather than silently accepted or silently dropped
  (RISK-UPDATE-CHANNEL-TRUST).
- `ANTI_CHEAT_REVIEW.md` — all 8 immutable boundaries from `docs/SAFETY.md` reviewed against the
  real architecture, with an explicit "held / held as policy only" verdict per boundary rather than
  a blanket restatement of the policy.
- `TBD_COMPONENTS.md` — 9 components named in open v2 issues with no implementation in `src/` yet,
  each with the specific threat-model debt it will owe once built.
- `tests/security/` — illustrative fixture files for 6 of the 15 abuse cases (see that directory's
  `README.md` for exactly what they are and are not).

## What #317 asks for that this pass does not close

Quoted or paraphrased directly from #317's acceptance criteria, matched against what actually
happened:

1. **"Complete an adversarial review of: data serialization... cryptographic operations... state
   machine transitions... external data sources... secret handling... authorization... error
   handling... platform boundaries."** This pass reviewed cryptographic operations (group key
   hashing, admin key comparison, DPAPI), secret handling (DPAPI store), and a slice of external
   data sources (TarkovTracker redirect handling, catalog integrity) as part of building the
   abuse-case and control register — but did not perform the full line-by-line review of
   serialization/deserialization surfaces, SQLite state-machine transitions, or Windows P/Invoke
   surfaces beyond DPAPI. `CONTROLS_AND_RESIDUAL_RISK.md`'s final section lists these explicitly.
2. **"No unresolved high or critical findings may be present at v2 release."** One High finding
   (RISK-UPDATE-CHANNEL-TRUST) is recorded, open, with no acceptance decision — by design, per
   `METHODOLOGY.md`'s rule that a finding may not be silently accepted. It needs an owner who can
   decide whether update-client version pinning is worth adding, which this pass is not positioned
   to decide unilaterally as a docs-only worktree. **This is the one item that most needs
   follow-up before #317 can close**, and it is called out here rather than buried in the register.
3. **Security review workflow and automation files** (#317's "Owned work" section names these
   alongside docs and fixtures). This pass added no `scripts/` or `.github/workflows/` changes —
   the task dispatching this work scoped it to `docs/security/**` and narrow `tests/security/**`
   fixtures specifically, excluding `scripts/audit-safety.sh` and workflow files as another
   agent's owned paths. Any new automation (a security-findings-summary CI step, wiring the
   fixtures into an executable test suite) is out of scope for this worktree.
4. **RISK-ANTICHEAT-REVIEW-DISCIPLINE's recommended tests** (deterministic assertions that new
   evidence surfaces in #305/#311 can't express a live-enemy concept) are recommendations for
   those issues' own implementation work, not something this docs-only pass can add without
   touching their owned source.

## Recommended next steps, in order

1. Get an owner and a decision on RISK-UPDATE-CHANNEL-TRUST (the one open High finding).
2. Wire the `tests/security/fixtures/` files into `TarkovCompanion.IntegrationTests` as actual
   assertions once an owner picks this up on `dev` or in CI — this pass deliberately did not do
   that, since it would require running `dotnet` locally, which is out of bounds for this
   worktree and this host per `AGENTS.md`.
3. Extend `CONTROLS_AND_RESIDUAL_RISK.md`'s "explicitly did not reach" list (serialization,
   SQLite state machine, broader platform boundaries) in a follow-up review pass.
4. Re-review `ANTI_CHEAT_REVIEW.md` boundary 8 once `docs/V2_CONTRACT.md` (#264) merges.
5. As each `TBD_COMPONENTS.md` entry gets implemented, extend the existing documents (new `TB-N`,
   new abuse cases, new register rows) rather than starting new ones.

## Verification performed for this pass

Consistent with the resource constraints in this task and `AGENTS.md`: no `dotnet`, MSBuild,
build, test, or package command was run on this host. No debugger, Docker, or VM was launched.
Everything in this document set is a **Reviewed**-tier claim (source read, reasoned about, not
executed) except the two CI-check citations (`audit-safety.sh`, `scan-secrets.sh` running in
`ci.yml`/`windows-verify.yml`), which are **Tested (automated)** because their execution is a fact
about those workflow files themselves, observable without running anything locally. Heavy
verification — running the fixtures as real tests, an actual adversarial pass with tooling — is
left serialized for GitHub Actions and the `dev` host, per `AGENTS.md`'s resource-safety rules.
