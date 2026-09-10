# Build status

Last updated: 2026-09-09

## Environment preflight

- Host: Omarchy 4.0.0 / Arch Linux, kernel `7.1.8-arch1-3`, x86-64.
- Git: 2.55.0.
- System .NET: runtimes 9.0.18 and 10.0.11; no SDK was installed.
- Isolated build SDK: official .NET SDK 10.0.401, runtime 10.0.12, SHA-512 verified against Microsoft's release metadata.
- Codex CLI: 0.153.4; safe noninteractive `codex exec --sandbox workspace-write -C <worktree> -` is available.
- Claude Code: 2.1.238; noninteractive `claude -p` and the `fable` alias are available.
- Windows validation: dockur Windows 11 VM is documented and currently stopped. It will not be started until the package and smoke harness are ready.
- Repository: `/home/cmannerow/Nextcloud/Documents/programming/tarkov-companion` because the active managed workspace permits writes under the programming root.

## Verified current baselines

- .NET SDK 10.0.401 / runtime 10.0.12 (released 2026-09-08).
- Avalonia 12.1.2.
- `json.tarkov.dev/endpoints` exposes barters, crafts, hideout, items, maps, price history, status, tasks, traders, and PvP season info.
- Supported modes reported by the endpoint catalog: `regular`, `pve`, `pvp-season`.
- Supported languages include English and the endpoint translation metadata is not uniform, so translation application is a shared data-layer concern.
- `the-hideout/tarkov-dev` reports MIT; `TarkovMonitor` reports GPL-3.0; RatScanner and SVG-map terms require manual notice review and are treated as no-copy references.

## Phase status

- [x] Handoff read completely.
- [x] Environment/tooling preflight.
- [x] Current endpoint and package versions checked.
- [x] Repository initialized.
- [x] Permanent safety rules written.
- [x] Foundation solution builds and tests (25 tests; 0 failed).
- [x] Private GitHub repository created at `smartpbx/tarkov-companion`; `main` tracks `origin/main`.
- [x] Repository registered in Orca with base ref `origin/main`.
- [x] Six isolated implementation tasks dispatched through Orca run `run_4bd33b0d6a32`.
- [ ] Data/economy/profile implementation.
- [ ] Core UI.
- [ ] Recognition.
- [ ] Raid/map/strategy.
- [ ] Windows platform.
- [ ] Simulator/integration.
- [ ] Independent reviews.
- [ ] Windows VM smoke.
- [ ] v1 package and release report.

## Hosted Windows verification

The packaged build is now launched and exercised on a GitHub-hosted Windows runner on every
push to the working branch. Method and current results are in `docs/WINDOWS_VERIFICATION.md`.

As of run 34535907475 the packaged application starts on a machine with no prior application
data, presents its window in about two seconds, stays responsive, downloads and stores the
full tarkov.dev catalogs, renders tarkov.dev map artwork, and exits cleanly. Real-EFT
behaviour remains unverified and deferred.

Six defects were found by launching the package that no fixture test could have caught,
because each fixture described a shape upstream no longer sends: a hideout trader requirement
that is now a comparison rather than a level, a quoted label rotation in the map catalog, an
empty translated item short name, duplicate extract identifiers, a view tile budget below
what a real map needs, and map images bound as file paths to a property that takes an image.

## Active decisions and limitations

- Code is MIT. Restrictively licensed map artwork is not embedded in the foundation; map providers retain explicit asset-level provenance and attribution.
- Real EFT validation cannot occur on this Linux boot or the VM and remains explicitly deferred.
