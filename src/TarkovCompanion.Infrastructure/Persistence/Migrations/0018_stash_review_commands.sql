-- #283: review intent used to live only in an in-process dictionary and disappeared on restart.
-- Commands stay append-only and separate from the captured evidence they may later correct.
CREATE TABLE stash_review_commands (
    command_id TEXT PRIMARY KEY NOT NULL CHECK(length(command_id) = 36),
    snapshot_id TEXT NOT NULL CHECK(length(snapshot_id) BETWEEN 1 AND 256),
    action INTEGER NOT NULL CHECK(action BETWEEN 1 AND 9),
    target_item_keys_json TEXT NOT NULL CHECK(json_valid(target_item_keys_json) AND length(target_item_keys_json) <= 66561),
    created_utc TEXT NOT NULL CHECK(length(created_utc) BETWEEN 20 AND 35),
    origin_identifier TEXT NOT NULL CHECK(length(origin_identifier) BETWEEN 1 AND 256),
    corrected_item_id TEXT CHECK(corrected_item_id IS NULL OR length(corrected_item_id) BETWEEN 1 AND 256),
    corrected_quantity INTEGER CHECK(corrected_quantity IS NULL OR corrected_quantity >= 1),
    reason TEXT CHECK(reason IS NULL OR length(reason) BETWEEN 1 AND 1024),
    CHECK((action = 1) = (corrected_item_id IS NOT NULL)),
    CHECK((action = 2) = (corrected_quantity IS NOT NULL))
);

CREATE INDEX idx_stash_review_commands_snapshot
    ON stash_review_commands(snapshot_id, created_utc, command_id);
