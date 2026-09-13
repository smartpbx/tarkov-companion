-- Sixteen tables that nothing in the application has ever read or written.
--
-- Every one of them was created by the first migration for a design that was later settled a
-- different way, and every one of them has sat empty since. That is not harmless: a table that
-- looks authoritative and is empty reads as a bug in whatever was supposed to fill it. It cost
-- a night twice -- once on raid_positions, where the raid trail turned out to live in
-- raid_events, and once on map_labels and map_render_configs, where the map's own labels and
-- floor layers turned out to be read straight out of the catalog payload in maps.source_json.
--
-- Where each one's data actually lives now:
--
--   app_meta                   schema_migrations and sync_state
--   map_labels                 maps.source_json, read by SqliteMapDefinitionCache
--   map_render_configs         maps.source_json
--   map_floor_layers           maps.source_json
--   item_icon_fingerprints     computed on demand by SkiaPerceptualIconMatcher
--   profile_trader_levels      the profile JSON file
--   profile_hideout_progress   the profile JSON file
--   profile_wishlist           the profile JSON file
--   profile_item_counts        quest_profile_item_holdings, which 0005 migrated it into
--   profile_overrides          the profile JSON file
--   event_definitions          the event JSON files
--   event_items                the event JSON files
--   profile_event_item_state   the event JSON files
--   key_intelligence_overrides nowhere -- curated key tiers were never built, and the comment
--                              in SqliteItemFactCatalog claiming this table is already
--                              preferred was wrong, which is its own small trap
--   raid_positions             raid_events, as rows of type "position"
--   raid_extracts              raid_events
--
-- Dropped rather than commented, because a comment saying "this is empty on purpose" is still
-- a table somebody has to read a comment about. No data is lost: nothing ever wrote to any of
-- them, and the one exception, profile_item_counts, was copied into the quest progress tables
-- by migration 0005, which always runs before this one.

-- Children first, so the drops hold whether or not foreign keys are being enforced.
DROP TABLE IF EXISTS profile_event_item_state;
DROP TABLE IF EXISTS event_items;
DROP TABLE IF EXISTS event_definitions;

DROP TABLE IF EXISTS app_meta;
DROP TABLE IF EXISTS map_labels;
DROP TABLE IF EXISTS map_render_configs;
DROP TABLE IF EXISTS map_floor_layers;
DROP TABLE IF EXISTS item_icon_fingerprints;
DROP TABLE IF EXISTS profile_trader_levels;
DROP TABLE IF EXISTS profile_hideout_progress;
DROP TABLE IF EXISTS profile_wishlist;
DROP TABLE IF EXISTS profile_item_counts;
DROP TABLE IF EXISTS profile_overrides;
DROP TABLE IF EXISTS key_intelligence_overrides;
DROP TABLE IF EXISTS raid_positions;
DROP TABLE IF EXISTS raid_extracts;
