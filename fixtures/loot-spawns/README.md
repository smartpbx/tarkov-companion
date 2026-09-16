# Loot-spawn import fixtures

`source-v1` is synthetic, test-only data. Its coordinates and item identifiers are deliberately
not claims about Escape from Tarkov. The fixture proves the versioned curated-bundle importer;
it must never be loaded as production map content.

`json.tarkov.dev` maps publish loose-loot positions and candidate item IDs, but this fixture is not
a copy of that data. Production content remains absent from the normalized importer until a
reproducible maps-to-bundle adapter is composed and any curated gap has completed provenance and
licence review. Item identity and value metadata continues to come from `json.tarkov.dev`.
