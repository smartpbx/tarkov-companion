CREATE TABLE IF NOT EXISTS quest_catalog_orphans (
    source_mode TEXT NOT NULL,
    profile_id TEXT NOT NULL,
    entity_kind TEXT NOT NULL CHECK (entity_kind IN ('task', 'objective')),
    external_id TEXT NOT NULL,
    recorded_value TEXT NOT NULL,
    detected_utc TEXT NOT NULL,
    PRIMARY KEY (source_mode, profile_id, entity_kind, external_id),
    FOREIGN KEY (profile_id) REFERENCES player_profiles(id) ON DELETE CASCADE
);
