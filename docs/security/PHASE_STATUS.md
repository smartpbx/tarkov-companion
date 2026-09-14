# Phase status

This records what the docs-only phase of #317 delivers and what remains. **This PR does not close
#317.** It establishes a source-grounded baseline and explicitly records current source/policy
conflicts; it does not make those findings safe by documenting them.

## What this phase delivers

- `SYSTEM_AND_TRUST_BOUNDARIES.md` — the current data flow and eleven named trust boundaries,
  including both pixel-ingress paths, plaintext group-key handling, full relay state shape,
  accepted LAN HTTP, unbounded cross-room marks, current import/export surfaces, catalog stale
  fallback, and the distinct relay/Desktop update implementations.
- `ASSETS_AND_ACTORS.md` — 14 assets and 12 actors, including a malicious/compromised relay
  operator and the actual local/in-process/network locations of the reusable group/admin keys.
- `ABUSE_CASES.md` — 24 concrete cases covering all eleven named boundaries; seven have illustrative
  JSON fixtures under `tests/security/fixtures/`.
- `CONTROLS_AND_RESIDUAL_RISK.md` — 16 open and three closed findings. Every open finding has an
  explicit Accept/Mitigate/Defer disposition. Six High findings remain open with required
  mitigation: room-key guessing, undetectable display-name impersonation, cleartext/receiver key
  disclosure, release-channel trust, current relay transmission of log-derived party data contrary
  to `docs/SAFETY.md`, and incomplete report redaction that can transmit paths/coordinates.
- `ANTI_CHEAT_REVIEW.md` — all eight immutable boundaries reviewed against current source, with
  lexical checks described as partial tripwires rather than certification.
- `TBD_COMPONENTS.md` — eleven future components or material rebuilds, each saying whether source is
  absent or a current implementation is expected to change.
- `README.md` — the progressive-disclosure product contract: the full version-matched register is
  reachable from Setup/Admin Data & Privacy, while routine UI stays concise without hiding active
  consent, outbound data, auth/transport state, active security/failure state, destructive impact,
  or decision-changing uncertainty/provenance.
- `tests/security/` — seven valid illustrative JSON files. They are not wired to a test runner and
  are not represented as executed behavior.

## What #317 still requires

1. **Complete adversarial implementation review.** This phase does not complete the requested
   line-by-line review of every serializer, migration/state transition, external dependency,
   error path, authorization path, or Windows P/Invoke lifetime. The remaining areas are listed
   in `CONTROLS_AND_RESIDUAL_RISK.md`.
2. **Mitigate and verify the open High findings.** Documentation is not mitigation. In
   particular, RISK-RELAY-OBSERVED-DATA-POLICY is a current source/current policy conflict and is
   release-blocking until product source stops the transmission or a separately authorized
   policy decision changes the contract outside this worktree. RISK-REPORT-REDACTION is likewise
   release-blocking until #281/#310 verify a complete assembled/persisted report excludes default
   paths, coordinate-bearing data, screenshot names, game logs, and credentials.
3. **Implement and review v2-only boundaries.** Pairing/local gateway, rebuilt operator/report
   lifecycle, evidence envelopes, historical model snapshots, new recognition, and Setup/Admin
   disclosure cannot be threat-modeled as completed systems before their designs exist. Each row
   in `TBD_COMPONENTS.md` names the review debt it creates.
4. **Executable security verification.** This worktree owns documentation and illustrative
   fixtures only. It does not add a security test project, workflow, or stronger static analyzer.
   Fixtures still need behavior assertions in the appropriate test suites, and anti-cheat data-
   contract assertions must land with #264/#305/#311.
5. **Release evidence.** GitHub Actions remains the integration gate. A configured workflow is not
   a passing run; #317 closeout must link exact-head runs and any required manual `dev` evidence.

## Recommended next steps

1. Resolve RISK-RELAY-OBSERVED-DATA-POLICY without silently relaxing `docs/SAFETY.md`.
2. Assign #304/#310 owners for authenticated confidential LAN transport, scoped pairing
   credentials, key-at-rest handling, relay/operator trust, global mark/request limits, and
   bounded report lifecycle.
3. Complete the Velopack/update-channel review and choose a tested rollback/downgrade policy.
4. Wire the seven fixtures into executable unit or purpose-built relay integration cases where
   they can assert actual handlers rather than prose.
5. Extend this living register as each `TBD_COMPONENTS.md` design lands; do not mark a planned
   boundary Reviewed before source exists.

## Verification performed for this correction

- No `dotnet`, MSBuild, build, product test, debugger, Docker, or VM command was run locally.
- All seven fixtures were parsed with `jq -e . tests/security/fixtures/*.json`.
- Abuse/risk and fixture/abuse identifier sets were compared in both directions with short shell
  extraction checks; every risk now has an abuse case and every abuse case has one risk.
- Documentation links/paths, `git diff --check`, `scripts/sweep-prose.sh`,
  `scripts/audit-safety.sh`, and `scripts/scan-secrets.sh` were run as short static checks only.

Those bullets report static commands, not product behavior. Exact-head GitHub Actions results are
recorded in PR #319 after the pushed correction and remain the required integration evidence.
