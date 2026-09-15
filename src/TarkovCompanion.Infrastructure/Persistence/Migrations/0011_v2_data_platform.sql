-- The V2 platform keeps raw endpoint bodies exactly once, compressed and addressed by the
-- SHA-256 of their uncompressed UTF-8 bytes. The old cache duplicated the full JSON in every
-- cache-key row, so its disposable HTTP bodies are intentionally replaced. Normalized catalog
-- rows and user state are unaffected, and the verified migration backup retains the old cache.
CREATE TABLE raw_endpoint_bodies (
    content_sha256 TEXT PRIMARY KEY CHECK (length(content_sha256) = 64 AND content_sha256 = lower(content_sha256)),
    compression TEXT NOT NULL CHECK (compression = 'gzip'),
    compressed_body BLOB NOT NULL,
    uncompressed_bytes INTEGER NOT NULL CHECK (uncompressed_bytes > 0),
    compressed_bytes INTEGER NOT NULL CHECK (compressed_bytes = length(compressed_body) AND compressed_bytes > 0),
    created_utc TEXT NOT NULL
);

DROP TABLE http_response_cache;
CREATE TABLE http_response_cache (
    cache_key TEXT PRIMARY KEY,
    content_sha256 TEXT NOT NULL REFERENCES raw_endpoint_bodies(content_sha256),
    cached_utc TEXT NOT NULL,
    last_accessed_utc TEXT NOT NULL,
    etag TEXT,
    last_modified TEXT
);
CREATE INDEX idx_http_cache_accessed ON http_response_cache(last_accessed_utc, cache_key);
CREATE INDEX idx_http_cache_content ON http_response_cache(content_sha256);

CREATE TABLE item_metrics_v2 (
    item_id TEXT PRIMARY KEY REFERENCES items(id) ON DELETE CASCADE,
    weight_kg REAL CHECK (weight_kg IS NULL OR (weight_kg >= 0 AND weight_kg <= 1000000)),
    flea_price_roubles INTEGER,
    trader_value_roubles INTEGER,
    measured_utc TEXT,
    source TEXT NOT NULL
);

CREATE TABLE price_history_unresolved_time (
    item_id TEXT NOT NULL REFERENCES items(id) ON DELETE CASCADE,
    source_ordinal INTEGER NOT NULL CHECK (source_ordinal >= 0),
    flea_price INTEGER,
    trader_value INTEGER,
    source TEXT NOT NULL,
    raw_json TEXT NOT NULL CHECK (json_valid(raw_json)),
    PRIMARY KEY (item_id, source, source_ordinal)
);

CREATE TABLE dataset_sync_runs (
    run_id TEXT PRIMARY KEY,
    game_mode TEXT NOT NULL,
    language TEXT NOT NULL,
    started_utc TEXT NOT NULL,
    completed_utc TEXT,
    state TEXT NOT NULL CHECK (state IN ('current', 'stale', 'refused', 'partial', 'last_known_good')),
    endpoint_count INTEGER NOT NULL CHECK (endpoint_count >= 0),
    successful_count INTEGER NOT NULL CHECK (successful_count >= 0),
    stale_count INTEGER NOT NULL CHECK (stale_count >= 0),
    refused_count INTEGER NOT NULL CHECK (refused_count >= 0),
    failure_count INTEGER NOT NULL CHECK (failure_count >= 0),
    detail TEXT
);

CREATE TABLE dataset_publications (
    publication_id TEXT PRIMARY KEY,
    run_id TEXT REFERENCES dataset_sync_runs(run_id),
    source_key TEXT NOT NULL,
    game_mode TEXT NOT NULL,
    language TEXT NOT NULL,
    state TEXT NOT NULL CHECK (state IN ('current', 'stale', 'refused', 'partial', 'last_known_good')),
    content_sha256 TEXT,
    record_count INTEGER CHECK (record_count IS NULL OR record_count >= 0),
    attempted_utc TEXT NOT NULL,
    published_utc TEXT,
    detail TEXT,
    previous_lkg_publication_id TEXT REFERENCES dataset_publications(publication_id)
);
CREATE INDEX idx_dataset_publications_scope_time
    ON dataset_publications(source_key, game_mode, language, attempted_utc DESC);

CREATE TABLE dataset_heads (
    source_key TEXT NOT NULL,
    game_mode TEXT NOT NULL,
    language TEXT NOT NULL,
    visible_publication_id TEXT REFERENCES dataset_publications(publication_id),
    last_known_good_publication_id TEXT REFERENCES dataset_publications(publication_id),
    state TEXT NOT NULL CHECK (state IN ('current', 'stale', 'refused', 'partial', 'last_known_good')),
    updated_utc TEXT NOT NULL,
    PRIMARY KEY (source_key, game_mode, language)
);

CREATE TABLE profile_workspaces (
    workspace_key INTEGER PRIMARY KEY CHECK (workspace_key = 1),
    revision INTEGER NOT NULL CHECK (revision >= 0),
    active_profile_id TEXT,
    updated_utc TEXT NOT NULL
);

CREATE TABLE profile_contexts (
    profile_id TEXT PRIMARY KEY,
    generation TEXT NOT NULL CHECK (length(trim(generation)) BETWEEN 1 AND 128),
    name TEXT NOT NULL CHECK (length(trim(name)) BETWEEN 1 AND 256),
    game_mode TEXT NOT NULL CHECK (game_mode IN ('Unknown', 'Pvp', 'Pve', 'Seasonal')),
    wipe_season TEXT NOT NULL,
    language TEXT NOT NULL,
    region TEXT NOT NULL,
    time_zone TEXT NOT NULL,
    data_snapshot_id TEXT NOT NULL,
    data_snapshot_published_utc TEXT NOT NULL,
    level INTEGER NOT NULL CHECK (level BETWEEN 1 AND 100),
    lifecycle TEXT NOT NULL CHECK (lifecycle IN ('Active', 'Archived')),
    updated_utc TEXT NOT NULL,
    extension_json TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(extension_json))
);
CREATE INDEX idx_profile_context_scope ON profile_contexts(game_mode, generation, lifecycle, profile_id);

CREATE TABLE profile_trader_progress_v2 (
    profile_id TEXT NOT NULL REFERENCES profile_contexts(profile_id) ON DELETE CASCADE,
    trader_id TEXT NOT NULL,
    level INTEGER NOT NULL CHECK (level BETWEEN 0 AND 4),
    PRIMARY KEY (profile_id, trader_id)
);
CREATE TABLE profile_completed_tasks_v2 (
    profile_id TEXT NOT NULL REFERENCES profile_contexts(profile_id) ON DELETE CASCADE,
    task_id TEXT NOT NULL,
    PRIMARY KEY (profile_id, task_id)
);
CREATE TABLE profile_objective_progress_v2 (
    profile_id TEXT NOT NULL REFERENCES profile_contexts(profile_id) ON DELETE CASCADE,
    objective_id TEXT NOT NULL,
    progress_count INTEGER NOT NULL CHECK (progress_count >= 0),
    PRIMARY KEY (profile_id, objective_id)
);
CREATE TABLE profile_hideout_progress_v2 (
    profile_id TEXT NOT NULL REFERENCES profile_contexts(profile_id) ON DELETE CASCADE,
    station_id TEXT NOT NULL,
    level INTEGER NOT NULL CHECK (level >= 0),
    PRIMARY KEY (profile_id, station_id)
);
CREATE TABLE profile_wishlist_v2 (
    profile_id TEXT NOT NULL REFERENCES profile_contexts(profile_id) ON DELETE CASCADE,
    item_id TEXT NOT NULL,
    PRIMARY KEY (profile_id, item_id)
);
CREATE TABLE profile_owned_counts_v2 (
    profile_id TEXT NOT NULL REFERENCES profile_contexts(profile_id) ON DELETE CASCADE,
    item_id TEXT NOT NULL,
    item_count INTEGER NOT NULL CHECK (item_count >= 0),
    PRIMARY KEY (profile_id, item_id)
);
CREATE TABLE profile_event_states_v2 (
    profile_id TEXT NOT NULL REFERENCES profile_contexts(profile_id) ON DELETE CASCADE,
    item_id TEXT NOT NULL,
    state TEXT NOT NULL,
    PRIMARY KEY (profile_id, item_id)
);
CREATE TABLE profile_item_overrides_v2 (
    profile_id TEXT NOT NULL REFERENCES profile_contexts(profile_id) ON DELETE CASCADE,
    item_id TEXT NOT NULL,
    value TEXT NOT NULL,
    PRIMARY KEY (profile_id, item_id)
);
CREATE TABLE profile_pins_v2 (
    profile_id TEXT NOT NULL REFERENCES profile_contexts(profile_id) ON DELETE CASCADE,
    target_kind TEXT NOT NULL,
    target_id TEXT NOT NULL,
    sort_order INTEGER NOT NULL CHECK (sort_order >= 0),
    note TEXT,
    PRIMARY KEY (profile_id, target_kind, target_id)
);

CREATE TABLE durable_outbox (
    operation_id TEXT PRIMARY KEY,
    idempotency_key TEXT NOT NULL UNIQUE,
    correlation_id TEXT NOT NULL,
    feature_id TEXT NOT NULL,
    command_kind INTEGER NOT NULL CHECK (command_kind BETWEEN 1 AND 8),
    version_major INTEGER NOT NULL CHECK (version_major BETWEEN 1 AND 99),
    version_minor INTEGER NOT NULL CHECK (version_minor BETWEEN 0 AND 999),
    aggregate_id TEXT NOT NULL,
    aggregate_sequence INTEGER NOT NULL CHECK (aggregate_sequence > 0),
    created_utc TEXT NOT NULL,
    not_before_utc TEXT NOT NULL,
    expires_utc TEXT NOT NULL,
    payload BLOB NOT NULL CHECK (length(payload) BETWEEN 1 AND 65536),
    max_attempts INTEGER NOT NULL CHECK (max_attempts BETWEEN 1 AND 100),
    attempt_timeout_ticks INTEGER NOT NULL CHECK (attempt_timeout_ticks > 0),
    initial_retry_delay_ticks INTEGER NOT NULL CHECK (initial_retry_delay_ticks >= 0),
    max_retry_delay_ticks INTEGER NOT NULL CHECK (max_retry_delay_ticks >= initial_retry_delay_ticks),
    backoff_factor REAL NOT NULL CHECK (backoff_factor BETWEEN 1 AND 10),
    delivery_state INTEGER NOT NULL CHECK (delivery_state BETWEEN 1 AND 5),
    attempt_count INTEGER NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
    next_attempt_utc TEXT NOT NULL,
    lease_token TEXT,
    lease_expires_utc TEXT,
    last_fault_json TEXT CHECK (last_fault_json IS NULL OR json_valid(last_fault_json)),
    completed_utc TEXT,
    dead_lettered_utc TEXT,
    UNIQUE (aggregate_id, aggregate_sequence)
);
CREATE INDEX idx_outbox_delivery ON durable_outbox(delivery_state, next_attempt_utc, created_utc);
CREATE INDEX idx_outbox_aggregate_head ON durable_outbox(aggregate_id, aggregate_sequence, delivery_state);
CREATE INDEX idx_outbox_completed ON durable_outbox(completed_utc) WHERE delivery_state = 5;

CREATE TABLE outbox_aggregate_sequences (
    aggregate_id TEXT PRIMARY KEY,
    next_sequence INTEGER NOT NULL CHECK (next_sequence > 0)
);

CREATE TABLE outbox_target_operations (
    operation_id TEXT PRIMARY KEY,
    command_kind INTEGER NOT NULL CHECK (command_kind BETWEEN 1 AND 8),
    target_id TEXT NOT NULL,
    applied_utc TEXT NOT NULL
);

CREATE TABLE observed_inventory_snapshots (
    snapshot_id TEXT PRIMARY KEY,
    profile_id TEXT NOT NULL,
    generation TEXT NOT NULL,
    game_mode TEXT NOT NULL,
    data_snapshot_id TEXT,
    observed_utc TEXT,
    recorded_utc TEXT NOT NULL,
    source TEXT NOT NULL,
    producer_version TEXT NOT NULL,
    coverage REAL CHECK (coverage IS NULL OR (coverage >= 0 AND coverage <= 1)),
    confidence REAL CHECK (confidence IS NULL OR (confidence >= 0 AND confidence <= 1)),
    is_current INTEGER NOT NULL CHECK (is_current IN (0, 1)),
    payload_json TEXT NOT NULL CHECK (json_valid(payload_json)),
    extension_json TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(extension_json))
);
CREATE INDEX idx_inventory_snapshot_scope
    ON observed_inventory_snapshots(profile_id, generation, game_mode, recorded_utc DESC);

CREATE TABLE observed_inventory_nodes (
    snapshot_id TEXT NOT NULL REFERENCES observed_inventory_snapshots(snapshot_id) ON DELETE CASCADE,
    node_id TEXT NOT NULL,
    parent_node_id TEXT,
    node_kind TEXT NOT NULL CHECK (node_kind IN ('stash', 'container', 'item', 'unknown')),
    item_id TEXT,
    grid_x INTEGER,
    grid_y INTEGER,
    width INTEGER,
    height INTEGER,
    item_count INTEGER CHECK (item_count IS NULL OR item_count >= 0),
    weight_kg REAL CHECK (weight_kg IS NULL OR (weight_kg >= 0 AND weight_kg <= 1000000)),
    confidence REAL CHECK (confidence IS NULL OR (confidence >= 0 AND confidence <= 1)),
    raw_json TEXT NOT NULL CHECK (json_valid(raw_json)),
    PRIMARY KEY (snapshot_id, node_id),
    FOREIGN KEY (snapshot_id, parent_node_id)
        REFERENCES observed_inventory_nodes(snapshot_id, node_id) ON DELETE CASCADE DEFERRABLE INITIALLY DEFERRED
);
CREATE INDEX idx_inventory_nodes_item ON observed_inventory_nodes(item_id, snapshot_id);
CREATE INDEX idx_inventory_nodes_parent ON observed_inventory_nodes(snapshot_id, parent_node_id);

CREATE TABLE raid_field_history (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    raid_id TEXT NOT NULL REFERENCES raids(id) ON DELETE CASCADE,
    field_name TEXT NOT NULL,
    value_json TEXT NOT NULL CHECK (json_valid(value_json)),
    provenance_kind TEXT NOT NULL CHECK (provenance_kind IN ('manual', 'observed')),
    source TEXT NOT NULL,
    observed_utc TEXT,
    recorded_utc TEXT NOT NULL,
    confidence REAL CHECK (confidence IS NULL OR (confidence >= 0 AND confidence <= 1))
);
CREATE INDEX idx_raid_field_history ON raid_field_history(raid_id, field_name, recorded_utc DESC);
CREATE INDEX idx_raids_profile_history ON raids(profile_id, start_utc DESC, id);

CREATE TABLE craft_history (
    history_id TEXT PRIMARY KEY,
    craft_id TEXT NOT NULL,
    station_id TEXT,
    station_level INTEGER,
    observed_utc TEXT,
    recorded_utc TEXT NOT NULL,
    output_item_id TEXT,
    output_count REAL,
    estimated_cost_roubles INTEGER,
    estimated_yield_roubles INTEGER,
    source TEXT NOT NULL,
    payload_json TEXT NOT NULL CHECK (json_valid(payload_json))
);
CREATE INDEX idx_craft_history_lookup ON craft_history(craft_id, observed_utc DESC, recorded_utc DESC);
CREATE INDEX idx_crafts_station_level ON crafts(station_id, level, id);

CREATE TABLE loadout_plans (
    plan_id TEXT PRIMARY KEY,
    profile_id TEXT NOT NULL,
    generation TEXT NOT NULL,
    game_mode TEXT NOT NULL,
    name TEXT NOT NULL,
    revision INTEGER NOT NULL CHECK (revision >= 0),
    updated_utc TEXT NOT NULL,
    payload_json TEXT NOT NULL CHECK (json_valid(payload_json)),
    extension_json TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(extension_json))
);
CREATE INDEX idx_loadout_plans_scope ON loadout_plans(profile_id, generation, game_mode, updated_utc DESC);

CREATE TABLE model_snapshots (
    model_snapshot_id TEXT PRIMARY KEY,
    profile_id TEXT,
    generation TEXT,
    game_mode TEXT,
    model_kind TEXT NOT NULL,
    presentation_kind TEXT NOT NULL CHECK (presentation_kind IN ('historical', 'modelled', 'predicted')),
    source TEXT NOT NULL,
    observed_utc TEXT,
    data_through_utc TEXT,
    generated_utc TEXT NOT NULL,
    coverage REAL CHECK (coverage IS NULL OR (coverage >= 0 AND coverage <= 1)),
    confidence REAL CHECK (confidence IS NULL OR (confidence >= 0 AND confidence <= 1)),
    calibration REAL CHECK (calibration IS NULL OR (calibration >= 0 AND calibration <= 1)),
    model_version TEXT NOT NULL,
    payload_json TEXT NOT NULL CHECK (json_valid(payload_json)),
    extension_json TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(extension_json))
);
CREATE INDEX idx_model_snapshots_scope
    ON model_snapshots(profile_id, generation, game_mode, model_kind, generated_utc DESC);

CREATE TABLE retention_policies (
    policy_key TEXT PRIMARY KEY,
    screenshot_retention_enabled INTEGER NOT NULL CHECK (screenshot_retention_enabled IN (0, 1)),
    screenshot_retention_hours INTEGER CHECK (screenshot_retention_hours IS NULL OR screenshot_retention_hours BETWEEN 1 AND 720),
    debug_capture_enabled INTEGER NOT NULL CHECK (debug_capture_enabled IN (0, 1)),
    data_retention_days INTEGER CHECK (data_retention_days IS NULL OR data_retention_days >= 1),
    updated_utc TEXT NOT NULL,
    extension_json TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(extension_json))
);

CREATE TABLE local_json_recovery (
    document_key TEXT PRIMARY KEY,
    state TEXT NOT NULL CHECK (state IN ('current', 'malformed', 'quarantined', 'missing')),
    detected_utc TEXT NOT NULL,
    content_sha256 TEXT,
    diagnostic_code TEXT NOT NULL
);

CREATE TABLE maintenance_schedules (
    operation TEXT PRIMARY KEY CHECK (operation IN ('vacuum', 'reindex', 'prune')),
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    interval_hours INTEGER NOT NULL CHECK (interval_hours BETWEEN 1 AND 8760),
    last_run_utc TEXT,
    next_run_utc TEXT,
    updated_utc TEXT NOT NULL
);

CREATE TABLE maintenance_history (
    run_id TEXT PRIMARY KEY,
    operation TEXT NOT NULL CHECK (operation IN ('vacuum', 'reindex', 'prune')),
    dry_run INTEGER NOT NULL CHECK (dry_run IN (0, 1)),
    started_utc TEXT NOT NULL,
    completed_utc TEXT,
    reclaimed_bytes INTEGER,
    affected_rows INTEGER,
    status TEXT NOT NULL CHECK (status IN ('planned', 'completed', 'failed')),
    diagnostic_code TEXT
);
