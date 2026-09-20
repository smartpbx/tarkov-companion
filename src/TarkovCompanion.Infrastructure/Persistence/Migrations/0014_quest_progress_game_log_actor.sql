-- Quest progress from the game's logs writes actor = 'GameLog'. Migration 0005's journal
-- CHECK only allowed User, Import, and SystemMigration, so every log-driven write failed with
-- SQLite error 19. SQLite cannot ALTER a CHECK constraint; rebuild the table and keep every row.

CREATE TABLE quest_progress_journal_v2 (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    profile_id TEXT NOT NULL,
    game_mode TEXT NOT NULL,
    generation TEXT NOT NULL,
    correlation_id TEXT NOT NULL,
    entity_kind TEXT NOT NULL CHECK (entity_kind IN ('Task', 'Objective', 'ItemHolding', 'Pin')),
    entity_id TEXT NOT NULL,
    field_name TEXT NOT NULL,
    previous_value_json TEXT NOT NULL CHECK (json_valid(previous_value_json)),
    new_value_json TEXT NOT NULL CHECK (json_valid(new_value_json)),
    inverse_value_json TEXT NOT NULL CHECK (json_valid(inverse_value_json)),
    actor TEXT NOT NULL CHECK (actor IN ('User', 'Import', 'SystemMigration', 'GameLog')),
    assertion_source TEXT NOT NULL,
    revision INTEGER NOT NULL CHECK (revision >= 0),
    recorded_utc TEXT NOT NULL,
    FOREIGN KEY (profile_id, game_mode, generation)
        REFERENCES quest_progress_profiles(profile_id, game_mode, generation)
);

INSERT INTO quest_progress_journal_v2(
    id,
    profile_id,
    game_mode,
    generation,
    correlation_id,
    entity_kind,
    entity_id,
    field_name,
    previous_value_json,
    new_value_json,
    inverse_value_json,
    actor,
    assertion_source,
    revision,
    recorded_utc)
SELECT
    id,
    profile_id,
    game_mode,
    generation,
    correlation_id,
    entity_kind,
    entity_id,
    field_name,
    previous_value_json,
    new_value_json,
    inverse_value_json,
    actor,
    assertion_source,
    revision,
    recorded_utc
FROM quest_progress_journal;

DROP TABLE quest_progress_journal;
ALTER TABLE quest_progress_journal_v2 RENAME TO quest_progress_journal;

CREATE TRIGGER quest_progress_journal_no_update
BEFORE UPDATE ON quest_progress_journal
BEGIN
    SELECT RAISE(ABORT, 'quest progress journal is append-only');
END;

CREATE TRIGGER quest_progress_journal_no_delete
BEFORE DELETE ON quest_progress_journal
BEGIN
    SELECT RAISE(ABORT, 'quest progress journal is append-only');
END;

CREATE INDEX idx_quest_progress_journal_scope_revision
    ON quest_progress_journal(profile_id, game_mode, generation, revision, id);
