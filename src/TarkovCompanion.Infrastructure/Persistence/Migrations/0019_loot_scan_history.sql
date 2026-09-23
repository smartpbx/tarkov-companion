-- #274/#282/#291: a completed Loot Scan used to live only on the page and was gone once the next
-- one replaced it, so an old call could not say which recommendation rules made it. Each scan is
-- kept with its calls as shown, the ruleset version and the raid it was taken in. No pixels.
-- raid_id has no foreign key: raid rows are written through the outbox and may land after the
-- scan, so a raid's hard delete removes its scans itself (SqliteRaidHistoryService).
CREATE TABLE loot_scans (
    scan_id TEXT PRIMARY KEY NOT NULL CHECK(length(scan_id) BETWEEN 1 AND 128),
    raid_id TEXT CHECK(raid_id IS NULL OR length(raid_id) = 36),
    map_id TEXT CHECK(map_id IS NULL OR length(map_id) BETWEEN 1 AND 128),
    evaluated_utc TEXT NOT NULL CHECK(length(evaluated_utc) BETWEEN 20 AND 35),
    ruleset_version TEXT NOT NULL CHECK(length(ruleset_version) BETWEEN 1 AND 128),
    is_complete INTEGER NOT NULL CHECK(is_complete IN (0, 1)),
    items_json TEXT NOT NULL CHECK(json_valid(items_json) AND length(items_json) <= 1048576)
);

CREATE INDEX idx_loot_scans_raid ON loot_scans(raid_id, evaluated_utc);
CREATE INDEX idx_loot_scans_evaluated ON loot_scans(evaluated_utc);
