-- Four map tables written on every sync and read by nothing.
--
-- Unlike the sixteen that 0007 dropped, these were never empty: RefreshMapsAsync writes one
-- awaited INSERT per feature of every map into them, inside the refresh's write transaction,
-- on every single sync. Thousands of rows an hour, for data no query has ever selected.
--
-- The map does draw hazards, loot, spawns and transits. It reads every one of them out of
-- maps.source_json -- SqliteMapFeatureCatalog opens with "SELECT source_json FROM maps" and
-- parses the payload -- so these four were a second, parallel copy that nothing consulted.
-- Dropping them changes nothing on screen.
--
-- Where each one's data actually lives now:
--
--   map_spawns          maps.source_json -> spawns[]
--   map_transits        maps.source_json -> transits[]
--   map_hazards         maps.source_json -> hazards[]
--   map_loot_positions  maps.source_json -> lootLoose[] and lootContainers[]
--
-- The sweep that found them (scripts/sweep-unread.sh) counted a DELETE as a read, which is why
-- it had never listed the eight craft, barter and trader tables. That is fixed in the same
-- change, and the sweep now fails on a new unread table rather than only reporting one.

DROP TABLE IF EXISTS map_spawns;
DROP TABLE IF EXISTS map_transits;
DROP TABLE IF EXISTS map_hazards;
DROP TABLE IF EXISTS map_loot_positions;

-- The write-ahead log is truncated after a checkpoint rather than growing without bound. It
-- had no limit, and this database has been as large as 114 MB.
PRAGMA journal_size_limit = 67108864;
