-- #712 1-12: what the player taught the companion by correcting it, kept on this PC only and
-- never exported or put in a diagnostic report. Setup counts and deletes all three.
-- learned_icon_references: the player's own icon crop (only the item's squares, never the
-- screen) for an item a scan could not name; the icon matcher consults it for refused cells.
CREATE TABLE learned_icon_references (
    reference_id TEXT PRIMARY KEY NOT NULL CHECK(length(reference_id) = 36),
    item_id TEXT NOT NULL CHECK(length(item_id) BETWEEN 1 AND 128),
    width_cells INTEGER NOT NULL CHECK(width_cells BETWEEN 1 AND 16),
    height_cells INTEGER NOT NULL CHECK(height_cells BETWEEN 1 AND 16),
    png BLOB NOT NULL CHECK(length(png) BETWEEN 1 AND 1048576),
    created_utc TEXT NOT NULL CHECK(length(created_utc) BETWEEN 20 AND 35)
);

CREATE INDEX idx_learned_icon_references_item ON learned_icon_references(item_id);

-- learned_text_aliases: an OCR reading the player said was a different item. An alias is used
-- once it has been picked twice (epic #712 T2).
CREATE TABLE learned_text_aliases (
    kind TEXT NOT NULL CHECK(length(kind) BETWEEN 1 AND 32),
    normalized_text TEXT NOT NULL CHECK(length(normalized_text) BETWEEN 1 AND 256),
    item_id TEXT NOT NULL CHECK(length(item_id) BETWEEN 1 AND 128),
    item_name TEXT NOT NULL CHECK(length(item_name) BETWEEN 1 AND 256),
    picks INTEGER NOT NULL CHECK(picks >= 1),
    updated_utc TEXT NOT NULL CHECK(length(updated_utc) BETWEEN 20 AND 35),
    PRIMARY KEY (kind, normalized_text, item_id)
);

-- learned_frame_corrections: a corrected cell or row of one screenshot, by the frame's content
-- hash, so the same frame read again shows the correction instead of the refusal.
CREATE TABLE learned_frame_corrections (
    frame_sha256 TEXT NOT NULL CHECK(length(frame_sha256) BETWEEN 1 AND 128),
    target_key TEXT NOT NULL CHECK(length(target_key) BETWEEN 1 AND 128),
    item_id TEXT NOT NULL CHECK(length(item_id) BETWEEN 1 AND 128),
    created_utc TEXT NOT NULL CHECK(length(created_utc) BETWEEN 20 AND 35),
    PRIMARY KEY (frame_sha256, target_key)
);
