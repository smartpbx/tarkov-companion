CREATE TABLE quest_catalog_snapshots (
    source_key TEXT NOT NULL,
    source_uri TEXT NOT NULL,
    local_game_mode TEXT NOT NULL,
    source_mode TEXT NOT NULL,
    language TEXT NOT NULL,
    payload_sha256 TEXT NOT NULL,
    translated_payload_sha256 TEXT NOT NULL,
    etag TEXT,
    last_modified_utc TEXT,
    fetched_utc TEXT NOT NULL,
    validated_utc TEXT NOT NULL,
    raw_json TEXT NOT NULL,
    translated_json TEXT NOT NULL,
    PRIMARY KEY (source_key, source_mode, language)
);

CREATE TABLE quest_catalog_tasks (
    source_key TEXT NOT NULL,
    source_mode TEXT NOT NULL,
    language TEXT NOT NULL,
    id TEXT NOT NULL,
    name TEXT NOT NULL,
    normalized_name TEXT,
    trader_id TEXT,
    min_player_level INTEGER,
    faction_name TEXT,
    primary_map_id TEXT,
    restartable INTEGER,
    kappa_required INTEGER,
    lightkeeper_required INTEGER,
    required_prestige_id TEXT,
    available_delay_seconds_min INTEGER,
    available_delay_seconds_max INTEGER,
    source_game_modes_json TEXT NOT NULL,
    raw_json TEXT NOT NULL,
    PRIMARY KEY (source_key, source_mode, language, id),
    FOREIGN KEY (source_key, source_mode, language)
        REFERENCES quest_catalog_snapshots(source_key, source_mode, language)
        ON DELETE CASCADE
);

CREATE TABLE quest_task_requirements (
    source_key TEXT NOT NULL,
    source_mode TEXT NOT NULL,
    language TEXT NOT NULL,
    task_id TEXT NOT NULL,
    source_ordinal INTEGER NOT NULL,
    required_task_id TEXT NOT NULL,
    raw_json TEXT NOT NULL,
    PRIMARY KEY (source_key, source_mode, language, task_id, source_ordinal),
    FOREIGN KEY (source_key, source_mode, language, task_id)
        REFERENCES quest_catalog_tasks(source_key, source_mode, language, id)
        ON DELETE CASCADE
);

CREATE TABLE quest_task_requirement_statuses (
    source_key TEXT NOT NULL,
    source_mode TEXT NOT NULL,
    language TEXT NOT NULL,
    task_id TEXT NOT NULL,
    requirement_ordinal INTEGER NOT NULL,
    status_ordinal INTEGER NOT NULL,
    required_status TEXT NOT NULL,
    PRIMARY KEY (
        source_key, source_mode, language, task_id,
        requirement_ordinal, status_ordinal
    ),
    FOREIGN KEY (
        source_key, source_mode, language, task_id, requirement_ordinal
    ) REFERENCES quest_task_requirements(
        source_key, source_mode, language, task_id, source_ordinal
    ) ON DELETE CASCADE
);

CREATE TABLE quest_catalog_objectives (
    source_key TEXT NOT NULL,
    source_mode TEXT NOT NULL,
    language TEXT NOT NULL,
    task_id TEXT NOT NULL,
    id TEXT NOT NULL,
    is_failure_condition INTEGER NOT NULL,
    source_ordinal INTEGER NOT NULL,
    source_type TEXT NOT NULL,
    normalized_kind TEXT NOT NULL,
    is_unsupported INTEGER NOT NULL,
    description TEXT NOT NULL,
    target_count TEXT,
    optional INTEGER,
    found_in_raid_required INTEGER,
    target_task_id TEXT,
    subtype_json TEXT NOT NULL,
    raw_json TEXT NOT NULL,
    PRIMARY KEY (
        source_key, source_mode, language, task_id, id, is_failure_condition
    ),
    FOREIGN KEY (source_key, source_mode, language, task_id)
        REFERENCES quest_catalog_tasks(source_key, source_mode, language, id)
        ON DELETE CASCADE
);

CREATE TABLE quest_objective_target_statuses (
    source_key TEXT NOT NULL,
    source_mode TEXT NOT NULL,
    language TEXT NOT NULL,
    task_id TEXT NOT NULL,
    objective_id TEXT NOT NULL,
    is_failure_condition INTEGER NOT NULL,
    status_ordinal INTEGER NOT NULL,
    target_status TEXT NOT NULL,
    PRIMARY KEY (
        source_key, source_mode, language, task_id, objective_id,
        is_failure_condition, status_ordinal
    ),
    FOREIGN KEY (
        source_key, source_mode, language, task_id, objective_id,
        is_failure_condition
    ) REFERENCES quest_catalog_objectives(
        source_key, source_mode, language, task_id, id,
        is_failure_condition
    ) ON DELETE CASCADE
);

CREATE TABLE quest_objective_map_links (
    source_key TEXT NOT NULL,
    source_mode TEXT NOT NULL,
    language TEXT NOT NULL,
    task_id TEXT NOT NULL,
    objective_id TEXT NOT NULL,
    is_failure_condition INTEGER NOT NULL,
    association_kind TEXT NOT NULL,
    source_ordinal INTEGER NOT NULL,
    map_id TEXT NOT NULL,
    PRIMARY KEY (
        source_key, source_mode, language, task_id, objective_id,
        is_failure_condition, association_kind, source_ordinal
    ),
    FOREIGN KEY (
        source_key, source_mode, language, task_id, objective_id,
        is_failure_condition
    ) REFERENCES quest_catalog_objectives(
        source_key, source_mode, language, task_id, id,
        is_failure_condition
    ) ON DELETE CASCADE
);

CREATE TABLE quest_objective_item_targets (
    source_key TEXT NOT NULL,
    source_mode TEXT NOT NULL,
    language TEXT NOT NULL,
    task_id TEXT NOT NULL,
    objective_id TEXT NOT NULL,
    is_failure_condition INTEGER NOT NULL,
    source_field TEXT NOT NULL,
    alternative_group INTEGER NOT NULL,
    source_ordinal INTEGER NOT NULL,
    item_id TEXT NOT NULL,
    target_count TEXT,
    found_in_raid_required INTEGER,
    PRIMARY KEY (
        source_key, source_mode, language, task_id, objective_id,
        is_failure_condition, source_field, alternative_group, source_ordinal
    ),
    FOREIGN KEY (
        source_key, source_mode, language, task_id, objective_id,
        is_failure_condition
    ) REFERENCES quest_catalog_objectives(
        source_key, source_mode, language, task_id, id,
        is_failure_condition
    ) ON DELETE CASCADE
);

CREATE TABLE quest_objective_zones (
    source_key TEXT NOT NULL,
    source_mode TEXT NOT NULL,
    language TEXT NOT NULL,
    task_id TEXT NOT NULL,
    objective_id TEXT NOT NULL,
    is_failure_condition INTEGER NOT NULL,
    source_ordinal INTEGER NOT NULL,
    source_zone_id TEXT,
    map_id TEXT,
    position_x REAL,
    position_y REAL,
    position_z REAL,
    outline_json TEXT NOT NULL,
    bottom_elevation REAL,
    top_elevation REAL,
    terrain_elevation REAL,
    size_json TEXT,
    name TEXT,
    raw_json TEXT NOT NULL,
    PRIMARY KEY (
        source_key, source_mode, language, task_id, objective_id,
        is_failure_condition, source_ordinal
    ),
    FOREIGN KEY (
        source_key, source_mode, language, task_id, objective_id,
        is_failure_condition
    ) REFERENCES quest_catalog_objectives(
        source_key, source_mode, language, task_id, id,
        is_failure_condition
    ) ON DELETE CASCADE
);

CREATE TABLE quest_catalog_orphans (
    source_mode TEXT NOT NULL,
    profile_id TEXT NOT NULL,
    entity_kind TEXT NOT NULL CHECK (entity_kind IN ('task', 'objective')),
    external_id TEXT NOT NULL,
    recorded_value TEXT NOT NULL,
    detected_utc TEXT NOT NULL,
    PRIMARY KEY (source_mode, profile_id, entity_kind, external_id),
    FOREIGN KEY (profile_id) REFERENCES player_profiles(id) ON DELETE CASCADE
);

CREATE INDEX idx_quest_catalog_tasks_id
    ON quest_catalog_tasks(id, source_mode, language);
CREATE INDEX idx_quest_catalog_objectives_id
    ON quest_catalog_objectives(id, source_mode, language);
CREATE INDEX idx_quest_objective_maps_map
    ON quest_objective_map_links(map_id, source_mode, language);
CREATE INDEX idx_quest_objective_items_item
    ON quest_objective_item_targets(item_id, source_mode, language);
CREATE INDEX idx_quest_objective_zones_map
    ON quest_objective_zones(map_id, source_mode, language);
