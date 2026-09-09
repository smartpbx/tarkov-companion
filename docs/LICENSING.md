# Licensing

Project source is MIT licensed. Dependency and asset obligations are tracked separately so project licensing never implies ownership of game data or third-party artwork.

## Reference policy

- `the-hideout/tarkov-dev`: GitHub reports MIT. Current configuration concepts may be consumed with attribution and provenance.
- `the-hideout/tarkov-dev-svg-maps`: GitHub cannot express the custom/Creative Commons terms as a simple SPDX identifier. Assets are optional, retain original attribution, and are not modified or bundled until the exact license obligations have been audited.
- RatScanner: license is not treated as permissive; source and assets are not copied.
- TarkovMonitor: GPL-3.0 reference only; no source is copied or linked.
- eft-ammo.com: inspiration/sanity checking only; no scraping or copied ranking/presentation.

## Release rule

`scripts/audit-licenses.sh` generates the final NuGet inventory. `docs/THIRD_PARTY_NOTICES.md` and any required `LICENSES/` texts must ship beside the Windows executable. Unknown or noncommercial asset terms block commercial distribution and must be called out in the release report.
