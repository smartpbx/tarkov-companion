CREATE TABLE quest_progress_imports (
    import_id TEXT PRIMARY KEY,
    profile_id TEXT NOT NULL,
    game_mode TEXT NOT NULL,
    generation TEXT NOT NULL,
    profile_name TEXT NOT NULL,
    payload_sha256 TEXT NOT NULL CHECK (length(payload_sha256) = 64),
    preview_sha256 TEXT NOT NULL CHECK (length(preview_sha256) = 64),
    base_revision INTEGER NOT NULL CHECK (base_revision >= 0),
    applied_revision INTEGER NOT NULL CHECK (applied_revision >= 0),
    source_app_version TEXT NOT NULL,
    source_exported_utc TEXT NOT NULL,
    provenance_summary TEXT NOT NULL,
    imported_utc TEXT NOT NULL,
    applied_change_count INTEGER NOT NULL CHECK (applied_change_count >= 0),
    kept_local_count INTEGER NOT NULL CHECK (kept_local_count >= 0),
    unresolved_count INTEGER NOT NULL CHECK (unresolved_count >= 0),
    UNIQUE (profile_id, game_mode, generation, payload_sha256),
    FOREIGN KEY (profile_id, game_mode, generation)
        REFERENCES quest_progress_profiles(profile_id, game_mode, generation)
);

CREATE TABLE quest_progress_import_conflicts (
    import_id TEXT NOT NULL,
    proposal_key TEXT NOT NULL,
    entity_kind TEXT NOT NULL,
    entity_id TEXT NOT NULL,
    local_value_json TEXT NOT NULL CHECK (json_valid(local_value_json)),
    incoming_value_json TEXT NOT NULL CHECK (json_valid(incoming_value_json)),
    reason TEXT NOT NULL,
    resolution TEXT NOT NULL CHECK (resolution IN ('KeepLocal', 'UseIncoming')),
    PRIMARY KEY (import_id, proposal_key),
    FOREIGN KEY (import_id) REFERENCES quest_progress_imports(import_id)
);

CREATE TABLE quest_progress_import_unresolved (
    import_id TEXT NOT NULL,
    proposal_key TEXT NOT NULL,
    entity_kind TEXT NOT NULL,
    entity_id TEXT NOT NULL,
    incoming_value_json TEXT NOT NULL CHECK (json_valid(incoming_value_json)),
    reason TEXT NOT NULL,
    PRIMARY KEY (import_id, proposal_key),
    FOREIGN KEY (import_id) REFERENCES quest_progress_imports(import_id)
);

CREATE TABLE quest_progress_import_undos (
    import_id TEXT PRIMARY KEY,
    undo_correlation_id TEXT NOT NULL UNIQUE,
    restored_revision INTEGER NOT NULL CHECK (restored_revision >= 0),
    restored_change_count INTEGER NOT NULL CHECK (restored_change_count >= 0),
    recorded_utc TEXT NOT NULL,
    FOREIGN KEY (import_id) REFERENCES quest_progress_imports(import_id)
);

CREATE INDEX idx_quest_progress_imports_scope_revision
    ON quest_progress_imports(profile_id, game_mode, generation, applied_revision, import_id);
