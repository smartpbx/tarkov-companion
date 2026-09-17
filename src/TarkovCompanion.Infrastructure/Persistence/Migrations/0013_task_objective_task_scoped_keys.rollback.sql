CREATE TABLE task_objectives_v1 (
    id TEXT PRIMARY KEY,
    task_id TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
    type TEXT NOT NULL,
    description TEXT NOT NULL,
    map_id TEXT,
    zone_json TEXT
);

INSERT OR REPLACE INTO task_objectives_v1(id, task_id, type, description, map_id, zone_json)
SELECT id, task_id, type, description, map_id, zone_json FROM task_objectives;

DROP TABLE task_objectives;
ALTER TABLE task_objectives_v1 RENAME TO task_objectives;

CREATE TABLE task_objective_items_v1 (
    objective_id TEXT NOT NULL REFERENCES task_objectives(id) ON DELETE CASCADE,
    item_id TEXT NOT NULL,
    count INTEGER NOT NULL,
    found_in_raid_required INTEGER NOT NULL,
    PRIMARY KEY (objective_id, item_id)
);

INSERT OR REPLACE INTO task_objective_items_v1(objective_id, item_id, count, found_in_raid_required)
SELECT objective_id, item_id, count, found_in_raid_required FROM task_objective_items;

DROP TABLE task_objective_items;
ALTER TABLE task_objective_items_v1 RENAME TO task_objective_items;
