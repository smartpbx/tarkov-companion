# API fixtures

These are small, hand-authored synthetic documents that exercise the public `json.tarkov.dev` envelope and field shapes. They do not contain copied production datasets, proprietary game assets, credentials, or user data.

The base and `_en` pairs cover translation paths for items, maps, tasks, hideout stations, and traders. Crafts, barters, and item price history cover the endpoint families that currently do not publish translation paths.

`tasks-contract.json` is a synthetic contract fixture covering the 20 objective discriminators observed in the regular task snapshot on 2026-09-10, plus one intentionally unknown discriminator and representative prerequisites, failure conditions, multi/no-map objectives, item alternatives, required-key groups, and zone geometry/elevation. Its production reference is recorded in ADR 0004; the production payload itself is not redistributed. Tests deterministically expand this contract to 515 tasks to exercise the observed catalog scale.
