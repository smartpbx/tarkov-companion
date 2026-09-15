# Licensing

Project source is MIT licensed. Dependency and asset obligations are tracked separately so project licensing never implies ownership of game data or third-party artwork.

## Reference policy

- `the-hideout/tarkov-dev`: GitHub reports MIT. Current configuration concepts may be consumed with attribution and provenance.
- `the-hideout/tarkov-dev-svg-maps`: map artwork is licensed CC BY-NC-SA 4.0 and carries an additional explicit prohibition on use in cheating/unfair-advantage software (including radar, ESP, cheat-client maps, automation, and pixel bots) with a stated revocation clause. Assets are optional runtime downloads, retain original attribution and license links, and are never bundled in source or release archives.
- RatScanner: license is not treated as permissive; source and assets are not copied.
- TarkovMonitor: GPL-3.0 reference only; no source is copied or linked.
- eft-ammo.com: inspiration/sanity checking only; no scraping or copied ranking/presentation.

## Reviewed extract coordinate facts

ADR 0012's extract supplement uses nine factual static name/world-coordinate marker records from
the SPT-DynamicMaps `Plugin/Resources/Maps` configurations at commit
[`4944764f5f6c42d152dca6bd1b5371c4f6212a9e`](https://github.com/acidphantasm/SPT-DynamicMaps/tree/4944764f5f6c42d152dca6bd1b5371c4f6212a9e/Plugin/Resources/Maps).
The original plugin license is MIT, copyright 2025 Michael P. Starkweather. Its map/data credit
file says that marker data was datamined by the program and credits TarkovTracker/tarkovData,
TarkovDev, and Shebuka for additional information. Exact copies are retained at
`LICENSES/SPT-DynamicMaps-MIT.txt` and
`LICENSES/SPT-DynamicMaps-map-and-data-credits.txt`.

No implementation, SVG or other map layer, artwork, icon, marker asset, wording, layout, or live
behavior is copied. In particular, the separately licensed Shebuka SVG layers named by the
credit file are outside this factual transcription. Permanent EFT Wiki revisions provide the
thirteen-map current-list comparison and corroborate supported names and sides, but no wiki prose
or assets are redistributed. The Lab list does not state a side for Medical Block Elevator, so
the compiled fact preserves `unknown`. Icebreaker's Helicopter and Terminal's Zubr Boat are
wiki-list facts with no coordinate and therefore cannot create markers. Each compiled fact links
its exact review source and records confidence and timestamps. The primary `json.tarkov.dev`
record supersedes the supplement by normalized identity when it appears.

## Offline OCR redistribution review

The production OCR provider uses `TesseractOCR` 5.5.2, a .NET wrapper containing
Tesseract 5.5.1 and Leptonica 1.85.0 Windows native binaries. The NuGet package
declares Apache-2.0 and identifies source commit
`787ebdb488184f47df3dcad7fe687b0b95d0d98c`. Its packaged x86 and x64 binaries
are distributed with the application; the win-x64 application requires the
Microsoft Visual C++ 2015-2022 x64 runtime on the target machine. Provider
self-test/availability state reports a missing runtime or native library
explicitly; it never substitutes scripted OCR.

The wrapper's Apache-2.0 declaration does not override the separate terms of its
native payload. Tesseract 5.5.1 is Apache-2.0. Leptonica 1.85.0 is BSD-2-Clause,
and binary inspection of both packaged architectures identifies statically linked
libjpeg-turbo 2.0.6 (`IJG AND BSD-3-Clause AND Zlib`), libpng 1.6.37 (`Libpng`),
libtiff 4.3.0 (`libtiff`), and zlib 1.2.11 (`Zlib`). The archive ships the exact
upstream license/README texts for each. The package provides no native-dependency
manifest or separate NOTICE, so the package metadata, repository commit, PE
imports, and identical embedded version/copyright strings in both architectures
are the available authoritative evidence for that prebuilt payload.

English recognition data is the `eng.traineddata` fast LSTM model from
`tesseract-ocr/tessdata_fast` commit
`87416418657359cb625c412a48b6e1d6d41c29bd`. The repository declares all model
data Apache-2.0. The exact vendored file SHA-256 is
`7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2`.
It is embedded in the Infrastructure assembly and materialized into a local cache
only when the production provider initializes; no network download occurs at
runtime.

Redistribution action: retain the wrapper, native OCR, codec, and model notices in
`docs/THIRD_PARTY_NOTICES.md` and ship every mapped file under `LICENSES/`.
Neither the TesseractOCR package nor the reviewed trained-data revision publishes
an additional NOTICE file. The provider and model are used through documented
APIs; no source was copied.

## Locked dependency inventory

`licenses/dependency-license-map.json` is the reviewed exact-version mapping.
`scripts/audit-licenses.sh` reads every restored source and test
`project.assets.json` without restoring or contacting the network, verifies that
the graph and mapping are a one-to-one match, requires every shipped entry to have
a non-empty `LICENSES/` mapping and notice anchor, and compares its deterministic
output with `docs/THIRD_PARTY_INVENTORY.json`. NuGet SHA-512 content hashes,
direct/transitive status, project reachability, and the bundled native/font/data
components are retained in that machine-readable inventory.

At the final graph, the audit resolves 52 runtime packages, one build-only package,
six non-Windows native-asset packages excluded from win-x64, 13 test-only packages,
and 13 separately inventoried bundled components. `scripts/test-license-audit.sh`
uses `fixtures/licenses/missing-mapping.*.json` to prove that an unmapped runtime
component fails the audit. Update the reviewed lock intentionally with
`scripts/audit-licenses.sh --write` after a restore; ordinary audit and packaging
must fail when dependency state drifts.

## Release rule

`scripts/package-windows.sh` deletes and recreates its exact staging directory,
publishes with `--no-restore`, reruns the locked license audit, and includes the
project `LICENSE`, this policy, `THIRD_PARTY_NOTICES.md`, the machine-readable
inventory, and the complete `LICENSES/` tree. It rejects portable/runtime data and
known map-cache metadata before creating a new archive, so stale archive entries
and runtime-cached map artwork cannot survive a package run. Unknown or
noncommercial asset terms block commercial distribution and must be called out in
the release report.

## Re-serving artwork, and item images

ADR 0007 reviews two questions this policy previously left open: whether the group relay may
serve cached map artwork to the tablet, and whether item icons may be downloaded to fingerprint
locally. In short: re-serving the map artwork **unmodified and attributed** is within CC
BY-NC-SA 4.0, which grants redistribution — "local use only" in ADR 0002 was this project's own
posture rather than a limit the licence imposed — on the conditions that attribution is visible
on the page that draws it, the bytes are not adapted, use stays non-commercial, and the
upstream anti-cheat prohibition is restated where it can be read. Item images are a weaker case:
nobody in the chain purports to license Battlestate Games' renders, so they may be cached and
hashed on the machine that uses them and neither the images nor the fingerprints derived from
them may be published, bundled, or served through the relay.

Runtime caching is not a transfer of ownership. Every cached map asset keeps the upstream URI, credited author/link, retrieval timestamp, SHA-256 content hash, `CC-BY-NC-SA-4.0` identifier, and Creative Commons license URI. A locally rasterized SVG preview remains cache-only and stays associated with the retained original metadata. The UI keeps attribution and license links visible for the selected map. Any distribution of cached, converted, or modified artwork must be separately reviewed for attribution, NonCommercial, ShareAlike, and upstream anti-cheat compliance.
