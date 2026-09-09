CREATE TABLE app_meta (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
);

CREATE TABLE sync_state (
    source_key TEXT NOT NULL,
    game_mode TEXT NOT NULL,
    language TEXT NOT NULL,
    last_success_utc TEXT,
    last_attempt_utc TEXT,
    etag TEXT,
    last_modified TEXT,
    content_hash TEXT,
    status TEXT NOT NULL,
    error_summary TEXT,
    PRIMARY KEY (source_key, game_mode, language)
);

CREATE TABLE items (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    short_name TEXT NOT NULL,
    normalized_name TEXT NOT NULL,
    description TEXT NOT NULL DEFAULT '',
    category_type TEXT NOT NULL,
    width INTEGER NOT NULL CHECK (width > 0),
    height INTEGER NOT NULL CHECK (height > 0),
    slots INTEGER NOT NULL CHECK (slots > 0),
    base_price INTEGER,
    avg_24h_price INTEGER,
    last_low_price INTEGER,
    flea_eligible INTEGER NOT NULL DEFAULT 0,
    icon_url TEXT,
    image_url TEXT,
    wiki_url TEXT,
    properties_type TEXT,
    properties_json TEXT,
    source_updated_utc TEXT NOT NULL,
    raw_json TEXT
);

CREATE TABLE item_categories (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL
);

CREATE TABLE item_category_membership (
    item_id TEXT NOT NULL REFERENCES items(id) ON DELETE CASCADE,
    category_id TEXT NOT NULL REFERENCES item_categories(id) ON DELETE CASCADE,
    PRIMARY KEY (item_id, category_id)
);

CREATE TABLE item_sell_offers (
    item_id TEXT NOT NULL REFERENCES items(id) ON DELETE CASCADE,
    vendor_id TEXT NOT NULL,
    vendor_name TEXT NOT NULL,
    value INTEGER NOT NULL,
    currency TEXT NOT NULL,
    requirements_json TEXT,
    updated_utc TEXT NOT NULL,
    PRIMARY KEY (item_id, vendor_id, currency)
);

CREATE VIRTUAL TABLE item_search USING fts5(
    item_id UNINDEXED,
    name,
    short_name,
    aliases,
    normalized_terms,
    tokenize = 'unicode61 remove_diacritics 2'
);

CREATE TABLE price_history (
    item_id TEXT NOT NULL REFERENCES items(id) ON DELETE CASCADE,
    timestamp_utc TEXT NOT NULL,
    flea_price INTEGER,
    trader_value INTEGER,
    source TEXT NOT NULL,
    PRIMARY KEY (item_id, timestamp_utc, source)
);

CREATE TABLE tasks (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    trader_id TEXT,
    min_level INTEGER,
    map_id TEXT,
    source_json TEXT NOT NULL
);

CREATE TABLE task_objectives (
    id TEXT PRIMARY KEY,
    task_id TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
    type TEXT NOT NULL,
    description TEXT NOT NULL,
    map_id TEXT,
    zone_json TEXT
);

CREATE TABLE task_objective_items (
    objective_id TEXT NOT NULL REFERENCES task_objectives(id) ON DELETE CASCADE,
    item_id TEXT NOT NULL,
    count INTEGER NOT NULL,
    found_in_raid_required INTEGER NOT NULL,
    PRIMARY KEY (objective_id, item_id)
);

CREATE TABLE hideout_stations (id TEXT PRIMARY KEY, name TEXT NOT NULL, source_json TEXT NOT NULL);
CREATE TABLE hideout_levels (
    station_id TEXT NOT NULL REFERENCES hideout_stations(id) ON DELETE CASCADE,
    level INTEGER NOT NULL,
    source_json TEXT NOT NULL,
    PRIMARY KEY (station_id, level)
);
CREATE TABLE hideout_requirements (
    station_id TEXT NOT NULL,
    level INTEGER NOT NULL,
    requirement_type TEXT NOT NULL,
    item_id TEXT,
    count INTEGER,
    metadata_json TEXT,
    FOREIGN KEY (station_id, level) REFERENCES hideout_levels(station_id, level) ON DELETE CASCADE
);

CREATE TABLE traders (id TEXT PRIMARY KEY, name TEXT NOT NULL, source_json TEXT NOT NULL);
CREATE TABLE trader_levels (
    trader_id TEXT NOT NULL REFERENCES traders(id) ON DELETE CASCADE,
    level INTEGER NOT NULL,
    source_json TEXT NOT NULL,
    PRIMARY KEY (trader_id, level)
);
CREATE TABLE trader_offers (id TEXT PRIMARY KEY, trader_id TEXT NOT NULL REFERENCES traders(id), source_json TEXT NOT NULL);

CREATE TABLE crafts (id TEXT PRIMARY KEY, station_id TEXT, level INTEGER, source_json TEXT NOT NULL);
CREATE TABLE craft_requirements (craft_id TEXT NOT NULL REFERENCES crafts(id) ON DELETE CASCADE, item_id TEXT, count INTEGER, source_json TEXT NOT NULL);
CREATE TABLE craft_outputs (craft_id TEXT NOT NULL REFERENCES crafts(id) ON DELETE CASCADE, item_id TEXT NOT NULL, count INTEGER NOT NULL, source_json TEXT NOT NULL);
CREATE TABLE barters (id TEXT PRIMARY KEY, trader_id TEXT, source_json TEXT NOT NULL);
CREATE TABLE barter_requirements (barter_id TEXT NOT NULL REFERENCES barters(id) ON DELETE CASCADE, item_id TEXT, count INTEGER, source_json TEXT NOT NULL);
CREATE TABLE barter_outputs (barter_id TEXT NOT NULL REFERENCES barters(id) ON DELETE CASCADE, item_id TEXT NOT NULL, count INTEGER NOT NULL, source_json TEXT NOT NULL);

CREATE TABLE maps (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    normalized_name TEXT NOT NULL,
    pmc_raid_duration_seconds INTEGER,
    scav_raid_duration_seconds INTEGER,
    source_json TEXT NOT NULL
);
CREATE TABLE map_spawns (id TEXT PRIMARY KEY, map_id TEXT NOT NULL REFERENCES maps(id) ON DELETE CASCADE, type TEXT NOT NULL, x REAL, y REAL, z REAL, source_json TEXT NOT NULL);
CREATE TABLE map_extracts (id TEXT PRIMARY KEY, map_id TEXT NOT NULL REFERENCES maps(id) ON DELETE CASCADE, name TEXT NOT NULL, x REAL, y REAL, z REAL, conditions TEXT, source_json TEXT NOT NULL);
CREATE TABLE map_transits (id TEXT PRIMARY KEY, map_id TEXT NOT NULL REFERENCES maps(id) ON DELETE CASCADE, source_json TEXT NOT NULL);
CREATE TABLE map_locks (id TEXT PRIMARY KEY, map_id TEXT NOT NULL REFERENCES maps(id) ON DELETE CASCADE, key_item_id TEXT, source_json TEXT NOT NULL);
CREATE TABLE map_hazards (id TEXT PRIMARY KEY, map_id TEXT NOT NULL REFERENCES maps(id) ON DELETE CASCADE, source_json TEXT NOT NULL);
CREATE TABLE map_loot_positions (id TEXT PRIMARY KEY, map_id TEXT NOT NULL REFERENCES maps(id) ON DELETE CASCADE, source_json TEXT NOT NULL);
CREATE TABLE map_labels (id TEXT PRIMARY KEY, map_id TEXT NOT NULL REFERENCES maps(id) ON DELETE CASCADE, name TEXT NOT NULL, source_json TEXT NOT NULL);
CREATE TABLE map_render_configs (
    map_id TEXT PRIMARY KEY REFERENCES maps(id) ON DELETE CASCADE,
    transform_json TEXT NOT NULL,
    rotation REAL NOT NULL DEFAULT 0,
    bounds_json TEXT NOT NULL,
    svg_uri TEXT,
    cached_asset_path TEXT,
    attribution TEXT,
    license TEXT,
    validated INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE map_floor_layers (
    map_id TEXT NOT NULL REFERENCES maps(id) ON DELETE CASCADE,
    layer_id TEXT NOT NULL,
    name TEXT NOT NULL,
    min_height REAL NOT NULL,
    max_height REAL NOT NULL,
    asset_reference TEXT,
    PRIMARY KEY (map_id, layer_id)
);

CREATE TABLE item_icon_fingerprints (
    item_id TEXT NOT NULL REFERENCES items(id) ON DELETE CASCADE,
    hash_type TEXT NOT NULL,
    hash TEXT NOT NULL,
    width INTEGER NOT NULL,
    height INTEGER NOT NULL,
    updated_utc TEXT NOT NULL,
    PRIMARY KEY (item_id, hash_type, width, height)
);
CREATE TABLE scan_history (
    id TEXT PRIMARY KEY,
    timestamp_utc TEXT NOT NULL,
    scan_context TEXT NOT NULL,
    resolved_item_id TEXT,
    confidence REAL NOT NULL,
    candidate_json TEXT NOT NULL,
    recommendation TEXT,
    source_geometry_json TEXT
);

CREATE TABLE player_profiles (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    game_mode TEXT NOT NULL,
    faction TEXT NOT NULL,
    level INTEGER NOT NULL,
    edition TEXT,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL
);
CREATE TABLE profile_trader_levels (profile_id TEXT NOT NULL REFERENCES player_profiles(id) ON DELETE CASCADE, trader_id TEXT NOT NULL, level INTEGER NOT NULL, PRIMARY KEY (profile_id, trader_id));
CREATE TABLE profile_task_progress (profile_id TEXT NOT NULL REFERENCES player_profiles(id) ON DELETE CASCADE, task_id TEXT NOT NULL, status TEXT NOT NULL, PRIMARY KEY (profile_id, task_id));
CREATE TABLE profile_objective_progress (profile_id TEXT NOT NULL REFERENCES player_profiles(id) ON DELETE CASCADE, objective_id TEXT NOT NULL, count INTEGER NOT NULL, PRIMARY KEY (profile_id, objective_id));
CREATE TABLE profile_hideout_progress (profile_id TEXT NOT NULL REFERENCES player_profiles(id) ON DELETE CASCADE, station_id TEXT NOT NULL, level INTEGER NOT NULL, PRIMARY KEY (profile_id, station_id));
CREATE TABLE profile_wishlist (profile_id TEXT NOT NULL REFERENCES player_profiles(id) ON DELETE CASCADE, item_id TEXT NOT NULL, PRIMARY KEY (profile_id, item_id));
CREATE TABLE profile_item_counts (profile_id TEXT NOT NULL REFERENCES player_profiles(id) ON DELETE CASCADE, item_id TEXT NOT NULL, count INTEGER NOT NULL, PRIMARY KEY (profile_id, item_id));
CREATE TABLE profile_overrides (profile_id TEXT NOT NULL REFERENCES player_profiles(id) ON DELETE CASCADE, item_id TEXT NOT NULL, action TEXT NOT NULL, note TEXT, PRIMARY KEY (profile_id, item_id));

CREATE TABLE event_definitions (event_id TEXT PRIMARY KEY, name TEXT NOT NULL, start_utc TEXT, end_utc TEXT, rules_json TEXT NOT NULL, active INTEGER NOT NULL, source_json TEXT NOT NULL);
CREATE TABLE event_items (event_id TEXT NOT NULL REFERENCES event_definitions(event_id) ON DELETE CASCADE, item_id TEXT NOT NULL, metadata_json TEXT, PRIMARY KEY (event_id, item_id));
CREATE TABLE profile_event_item_state (profile_id TEXT NOT NULL REFERENCES player_profiles(id) ON DELETE CASCADE, event_id TEXT NOT NULL REFERENCES event_definitions(event_id) ON DELETE CASCADE, item_id TEXT NOT NULL, state TEXT NOT NULL, updated_utc TEXT NOT NULL, PRIMARY KEY (profile_id, event_id, item_id));

CREATE TABLE key_intelligence_overrides (
    key_item_id TEXT PRIMARY KEY,
    score_json TEXT NOT NULL,
    notes TEXT NOT NULL,
    source TEXT NOT NULL,
    source_date TEXT NOT NULL,
    confidence REAL NOT NULL
);

CREATE TABLE raids (
    id TEXT PRIMARY KEY,
    profile_id TEXT NOT NULL REFERENCES player_profiles(id),
    map_id TEXT,
    mode TEXT NOT NULL,
    start_utc TEXT,
    end_utc TEXT,
    outcome TEXT,
    manual_metadata_json TEXT,
    notes TEXT
);
CREATE TABLE raid_events (id INTEGER PRIMARY KEY AUTOINCREMENT, raid_id TEXT NOT NULL REFERENCES raids(id) ON DELETE CASCADE, timestamp_utc TEXT NOT NULL, type TEXT NOT NULL, payload_json TEXT NOT NULL);
CREATE TABLE raid_positions (id INTEGER PRIMARY KEY AUTOINCREMENT, raid_id TEXT NOT NULL REFERENCES raids(id) ON DELETE CASCADE, timestamp_utc TEXT NOT NULL, x REAL NOT NULL, y REAL NOT NULL, z REAL NOT NULL, heading REAL NOT NULL, floor TEXT, screenshot_filename TEXT NOT NULL);
CREATE TABLE raid_extracts (raid_id TEXT NOT NULL REFERENCES raids(id) ON DELETE CASCADE, extract_id TEXT NOT NULL, name TEXT NOT NULL, confidence REAL NOT NULL, source TEXT NOT NULL, PRIMARY KEY (raid_id, extract_id));

CREATE INDEX idx_items_normalized_name ON items(normalized_name);
CREATE INDEX idx_price_history_item_timestamp ON price_history(item_id, timestamp_utc DESC);
CREATE INDEX idx_map_extracts_map ON map_extracts(map_id);
CREATE INDEX idx_raid_events_raid_timestamp ON raid_events(raid_id, timestamp_utc);
CREATE INDEX idx_raid_positions_raid_timestamp ON raid_positions(raid_id, timestamp_utc);
