DROP INDEX IF EXISTS idx_craft_outputs_craft;
DROP INDEX IF EXISTS idx_craft_requirements_craft;
DROP INDEX IF EXISTS idx_crafts_station_level;
DROP INDEX IF EXISTS idx_raids_profile_history;
DROP INDEX IF EXISTS idx_raid_field_history_recent;
DROP INDEX IF EXISTS idx_dataset_publications_run_state;
DROP TABLE IF EXISTS maintenance_history;
DROP TABLE IF EXISTS maintenance_schedules;
DROP TABLE IF EXISTS local_json_recovery;
DROP TABLE IF EXISTS retention_policies;
DROP TABLE IF EXISTS model_snapshots;
DROP TABLE IF EXISTS loadout_plans;
DROP TABLE IF EXISTS craft_history;
DROP TABLE IF EXISTS raid_field_history;
DROP TABLE IF EXISTS observed_inventory_nodes;
DROP TABLE IF EXISTS observed_inventory_snapshots;
DROP TABLE IF EXISTS outbox_target_operations;
DROP TABLE IF EXISTS outbox_aggregate_sequences;
DROP TABLE IF EXISTS durable_outbox;
DROP TABLE IF EXISTS profile_pins_v2;
DROP TABLE IF EXISTS profile_item_overrides_v2;
DROP TABLE IF EXISTS profile_event_states_v2;
DROP TABLE IF EXISTS profile_owned_counts_v2;
DROP TABLE IF EXISTS profile_wishlist_v2;
DROP TABLE IF EXISTS profile_hideout_progress_v2;
DROP TABLE IF EXISTS profile_objective_progress_v2;
DROP TABLE IF EXISTS profile_completed_tasks_v2;
DROP TABLE IF EXISTS profile_trader_progress_v2;
DROP TABLE IF EXISTS profile_contexts;
DROP TABLE IF EXISTS profile_workspaces;
DROP TABLE IF EXISTS dataset_endpoint_materializations;
DROP TABLE IF EXISTS dataset_heads;
DROP TABLE IF EXISTS dataset_publications;
DROP TABLE IF EXISTS dataset_sync_runs;
DROP TABLE IF EXISTS price_history_unresolved_time;
DROP TABLE IF EXISTS item_metrics_v2;
DROP TABLE IF EXISTS http_response_cache;
DROP TABLE IF EXISTS raw_endpoint_bodies;
CREATE TABLE http_response_cache (
    cache_key TEXT PRIMARY KEY,
    body_json TEXT NOT NULL,
    cached_utc TEXT NOT NULL,
    etag TEXT,
    last_modified TEXT
);
