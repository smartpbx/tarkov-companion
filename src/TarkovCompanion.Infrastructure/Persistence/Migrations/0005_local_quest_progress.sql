CREATE TABLE quest_progress_profiles (
    profile_id TEXT NOT NULL,
    game_mode TEXT NOT NULL CHECK (game_mode IN ('Regular', 'Pve', 'PvpSeason')),
    generation TEXT NOT NULL CHECK (length(trim(generation)) BETWEEN 1 AND 128),
    display_name TEXT NOT NULL CHECK (length(trim(display_name)) > 0),
    revision INTEGER NOT NULL DEFAULT 0 CHECK (revision >= 0),
    created_utc TEXT NOT NULL,
    modified_utc TEXT NOT NULL,
    PRIMARY KEY (profile_id, game_mode, generation)
);

CREATE TABLE quest_profile_task_states (
    profile_id TEXT NOT NULL,
    game_mode TEXT NOT NULL,
    generation TEXT NOT NULL,
    task_id TEXT NOT NULL CHECK (length(trim(task_id)) > 0),
    state TEXT NOT NULL CHECK (state IN ('Unknown', 'NotStarted', 'Active', 'Completed', 'Failed')),
    assertion_source TEXT NOT NULL,
    revision INTEGER NOT NULL CHECK (revision >= 0),
    modified_utc TEXT NOT NULL,
    PRIMARY KEY (profile_id, game_mode, generation, task_id),
    FOREIGN KEY (profile_id, game_mode, generation)
        REFERENCES quest_progress_profiles(profile_id, game_mode, generation)
        ON DELETE CASCADE
);

CREATE TABLE quest_profile_objective_states (
    profile_id TEXT NOT NULL,
    game_mode TEXT NOT NULL,
    generation TEXT NOT NULL,
    objective_id TEXT NOT NULL CHECK (length(trim(objective_id)) > 0),
    state TEXT NOT NULL CHECK (state IN ('Unknown', 'InProgress', 'Completed')),
    progress_count TEXT CHECK (
        progress_count IS NULL OR
        (CAST(progress_count AS REAL) >= 0 AND lower(progress_count) NOT IN ('nan', 'infinity', '-infinity'))
    ),
    assertion_source TEXT NOT NULL,
    revision INTEGER NOT NULL CHECK (revision >= 0),
    modified_utc TEXT NOT NULL,
    PRIMARY KEY (profile_id, game_mode, generation, objective_id),
    FOREIGN KEY (profile_id, game_mode, generation)
        REFERENCES quest_progress_profiles(profile_id, game_mode, generation)
        ON DELETE CASCADE
);

CREATE TABLE quest_profile_item_holdings (
    profile_id TEXT NOT NULL,
    game_mode TEXT NOT NULL,
    generation TEXT NOT NULL,
    item_id TEXT NOT NULL CHECK (length(trim(item_id)) > 0),
    found_in_raid INTEGER NOT NULL CHECK (found_in_raid IN (0, 1)),
    item_count INTEGER NOT NULL CHECK (item_count >= 0),
    assertion_source TEXT NOT NULL,
    revision INTEGER NOT NULL CHECK (revision >= 0),
    modified_utc TEXT NOT NULL,
    PRIMARY KEY (profile_id, game_mode, generation, item_id, found_in_raid),
    FOREIGN KEY (profile_id, game_mode, generation)
        REFERENCES quest_progress_profiles(profile_id, game_mode, generation)
        ON DELETE CASCADE
);

CREATE TABLE quest_profile_pins (
    profile_id TEXT NOT NULL,
    game_mode TEXT NOT NULL,
    generation TEXT NOT NULL,
    target_kind TEXT NOT NULL CHECK (target_kind IN ('Task', 'Objective')),
    target_id TEXT NOT NULL CHECK (length(trim(target_id)) > 0),
    sort_order INTEGER NOT NULL,
    note TEXT,
    assertion_source TEXT NOT NULL,
    revision INTEGER NOT NULL CHECK (revision >= 0),
    modified_utc TEXT NOT NULL,
    PRIMARY KEY (profile_id, game_mode, generation, target_kind, target_id),
    FOREIGN KEY (profile_id, game_mode, generation)
        REFERENCES quest_progress_profiles(profile_id, game_mode, generation)
        ON DELETE CASCADE
);

CREATE TABLE quest_progress_journal (
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
    actor TEXT NOT NULL CHECK (actor IN ('User', 'Import', 'SystemMigration')),
    assertion_source TEXT NOT NULL,
    revision INTEGER NOT NULL CHECK (revision >= 0),
    recorded_utc TEXT NOT NULL,
    FOREIGN KEY (profile_id, game_mode, generation)
        REFERENCES quest_progress_profiles(profile_id, game_mode, generation)
);

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

CREATE INDEX idx_quest_task_states_scope_state
    ON quest_profile_task_states(profile_id, game_mode, generation, state, task_id);
CREATE INDEX idx_quest_objective_states_scope
    ON quest_profile_objective_states(profile_id, game_mode, generation, objective_id);
CREATE INDEX idx_quest_item_holdings_scope_item
    ON quest_profile_item_holdings(profile_id, game_mode, generation, item_id);
CREATE INDEX idx_quest_progress_journal_scope_revision
    ON quest_progress_journal(profile_id, game_mode, generation, revision, id);

-- Preserve any rows written through the original, uncomposed profile tables.
-- Unknown legacy statuses remain Unknown; objective counts never imply completion.
INSERT OR IGNORE INTO quest_progress_profiles(
    profile_id, game_mode, generation, display_name, revision, created_utc, modified_utc)
SELECT
    id,
    CASE lower(game_mode)
        WHEN 'regular' THEN 'Regular'
        WHEN 'pve' THEN 'Pve'
        WHEN 'pvpseason' THEN 'PvpSeason'
        WHEN 'pvp-season' THEN 'PvpSeason'
        ELSE NULL
    END,
    substr('legacy-' || lower(replace(id, '-', '')), 1, 128),
    CASE WHEN length(trim(name)) > 0 THEN name ELSE 'Legacy profile' END,
    0,
    created_utc,
    updated_utc
FROM player_profiles
WHERE lower(game_mode) IN ('regular', 'pve', 'pvpseason', 'pvp-season');

INSERT OR IGNORE INTO quest_profile_task_states(
    profile_id, game_mode, generation, task_id, state, assertion_source, revision, modified_utc)
SELECT
    profile.id,
    scope.game_mode,
    scope.generation,
    progress.task_id,
    CASE lower(progress.status)
        WHEN 'notstarted' THEN 'NotStarted'
        WHEN 'not-started' THEN 'NotStarted'
        WHEN 'active' THEN 'Active'
        WHEN 'complete' THEN 'Completed'
        WHEN 'completed' THEN 'Completed'
        WHEN 'failed' THEN 'Failed'
        ELSE 'Unknown'
    END,
    'Legacy database migration',
    0,
    profile.updated_utc
FROM profile_task_progress AS progress
JOIN player_profiles AS profile ON profile.id = progress.profile_id
JOIN quest_progress_profiles AS scope ON scope.profile_id = profile.id;

INSERT OR IGNORE INTO quest_profile_objective_states(
    profile_id, game_mode, generation, objective_id, state, progress_count,
    assertion_source, revision, modified_utc)
SELECT
    profile.id,
    scope.game_mode,
    scope.generation,
    progress.objective_id,
    'InProgress',
    CAST(progress.count AS TEXT),
    'Legacy database migration',
    0,
    profile.updated_utc
FROM profile_objective_progress AS progress
JOIN player_profiles AS profile ON profile.id = progress.profile_id
JOIN quest_progress_profiles AS scope ON scope.profile_id = profile.id
WHERE progress.count >= 0;

INSERT OR IGNORE INTO quest_profile_item_holdings(
    profile_id, game_mode, generation, item_id, found_in_raid, item_count,
    assertion_source, revision, modified_utc)
SELECT
    profile.id,
    scope.game_mode,
    scope.generation,
    counts.item_id,
    0,
    counts.count,
    'Legacy database migration',
    0,
    profile.updated_utc
FROM profile_item_counts AS counts
JOIN player_profiles AS profile ON profile.id = counts.profile_id
JOIN quest_progress_profiles AS scope ON scope.profile_id = profile.id
WHERE counts.count >= 0;

INSERT INTO quest_progress_journal(
    profile_id, game_mode, generation, correlation_id, entity_kind, entity_id,
    field_name, previous_value_json, new_value_json, inverse_value_json,
    actor, assertion_source, revision, recorded_utc)
SELECT
    state.profile_id,
    state.game_mode,
    state.generation,
    '00000000-0000-0000-0000-000000000005',
    'Task',
    state.task_id,
    'state',
    'null',
    json_object('state', state.state),
    'null',
    'SystemMigration',
    state.assertion_source,
    0,
    state.modified_utc
FROM quest_profile_task_states AS state
WHERE state.assertion_source = 'Legacy database migration';

INSERT INTO quest_progress_journal(
    profile_id, game_mode, generation, correlation_id, entity_kind, entity_id,
    field_name, previous_value_json, new_value_json, inverse_value_json,
    actor, assertion_source, revision, recorded_utc)
SELECT
    state.profile_id,
    state.game_mode,
    state.generation,
    '00000000-0000-0000-0000-000000000005',
    'Objective',
    state.objective_id,
    'progress',
    'null',
    json_object('state', state.state, 'count', CAST(state.progress_count AS REAL)),
    'null',
    'SystemMigration',
    state.assertion_source,
    0,
    state.modified_utc
FROM quest_profile_objective_states AS state
WHERE state.assertion_source = 'Legacy database migration';

INSERT INTO quest_progress_journal(
    profile_id, game_mode, generation, correlation_id, entity_kind, entity_id,
    field_name, previous_value_json, new_value_json, inverse_value_json,
    actor, assertion_source, revision, recorded_utc)
SELECT
    holding.profile_id,
    holding.game_mode,
    holding.generation,
    '00000000-0000-0000-0000-000000000005',
    'ItemHolding',
    holding.item_id,
    CASE holding.found_in_raid WHEN 1 THEN 'foundInRaidCount' ELSE 'nonFoundInRaidCount' END,
    'null',
    json_object('count', holding.item_count, 'foundInRaid', holding.found_in_raid),
    'null',
    'SystemMigration',
    holding.assertion_source,
    0,
    holding.modified_utc
FROM quest_profile_item_holdings AS holding
WHERE holding.assertion_source = 'Legacy database migration';
