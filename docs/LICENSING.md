# Licensing

Project source is MIT licensed. Dependency and asset obligations are tracked separately so project licensing never implies ownership of game data or third-party artwork.

## Reference policy

- `the-hideout/tarkov-dev`: GitHub reports MIT. Current configuration concepts may be consumed with attribution and provenance.
- `the-hideout/tarkov-dev-svg-maps`: map artwork is licensed CC BY-NC-SA 4.0 and carries an additional explicit prohibition on use in cheating/unfair-advantage software (including radar, ESP, cheat-client maps, automation, and pixel bots) with a stated revocation clause. Assets are optional runtime downloads, retain original attribution and license links, and are never bundled in source or release archives.
- RatScanner: license is not treated as permissive; source and assets are not copied.
- TarkovMonitor: GPL-3.0 reference only; no source is copied or linked.
- eft-ammo.com: inspiration/sanity checking only; no scraping or copied ranking/presentation.

## Release rule

`scripts/audit-licenses.sh` generates the final NuGet inventory. `docs/THIRD_PARTY_NOTICES.md` and any required `LICENSES/` texts must ship beside the Windows executable. Unknown or noncommercial asset terms block commercial distribution and must be called out in the release report.

Runtime caching is not a transfer of ownership. Every cached map asset keeps the upstream URI, credited author/link, retrieval timestamp, SHA-256 content hash, `CC-BY-NC-SA-4.0` identifier, and Creative Commons license URI. A locally rasterized SVG preview remains cache-only and stays associated with the retained original metadata. The UI keeps attribution and license links visible for the selected map. Any distribution of cached, converted, or modified artwork must be separately reviewed for attribution, NonCommercial, ShareAlike, and upstream anti-cheat compliance.
