# Licensing

Project source is MIT licensed. Dependency and asset obligations are tracked separately so project licensing never implies ownership of game data or third-party artwork.

## Reference policy

- `the-hideout/tarkov-dev`: GitHub reports MIT. Current configuration concepts may be consumed with attribution and provenance.
- `the-hideout/tarkov-dev-svg-maps`: map artwork is licensed CC BY-NC-SA 4.0 and carries an additional explicit prohibition on use in cheating/unfair-advantage software (including radar, ESP, cheat-client maps, automation, and pixel bots) with a stated revocation clause. Assets are optional runtime downloads, retain original attribution and license links, and are never bundled in source or release archives.
- RatScanner: license is not treated as permissive; source and assets are not copied.
- TarkovMonitor: GPL-3.0 reference only; no source is copied or linked.
- eft-ammo.com: inspiration/sanity checking only; no scraping or copied ranking/presentation.

## Offline OCR redistribution review

The production OCR provider uses `TesseractOCR` 5.5.2, a .NET wrapper containing
Tesseract 5.5.1 and Leptonica 1.85.0 Windows native binaries. The NuGet package
declares Apache-2.0 and identifies source commit
`787ebdb488184f47df3dcad7fe687b0b95d0d98c`. Its packaged x64 binaries are
distributed with the application and require the Microsoft Visual C++ 2015-2022
x64 runtime on the target machine. Provider self-test/availability state reports a
missing runtime or native library explicitly; it never substitutes scripted OCR.

English recognition data is the `eng.traineddata` fast LSTM model from
`tesseract-ocr/tessdata_fast` commit
`87416418657359cb625c412a48b6e1d6d41c29bd`. The repository declares all model
data Apache-2.0. The exact vendored file SHA-256 is
`7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2`.
It is embedded in the Infrastructure assembly and materialized into a local cache
only when the production provider initializes; no network download occurs at
runtime.

Redistribution action: retain the Apache-2.0 notices in
`docs/THIRD_PARTY_NOTICES.md` and ship the full license text in
`LICENSES/Apache-2.0.txt`. Neither upstream repository publishes an additional
NOTICE file in the reviewed package/model revision. The provider and model are
used through documented APIs; no source was copied.

## Release rule

`scripts/audit-licenses.sh` generates the final NuGet inventory. `docs/THIRD_PARTY_NOTICES.md` and any required `LICENSES/` texts must ship beside the Windows executable. Unknown or noncommercial asset terms block commercial distribution and must be called out in the release report.

Runtime caching is not a transfer of ownership. Every cached map asset keeps the upstream URI, credited author/link, retrieval timestamp, SHA-256 content hash, `CC-BY-NC-SA-4.0` identifier, and Creative Commons license URI. A locally rasterized SVG preview remains cache-only and stays associated with the retained original metadata. The UI keeps attribution and license links visible for the selected map. Any distribution of cached, converted, or modified artwork must be separately reviewed for attribution, NonCommercial, ShareAlike, and upstream anti-cheat compliance.
