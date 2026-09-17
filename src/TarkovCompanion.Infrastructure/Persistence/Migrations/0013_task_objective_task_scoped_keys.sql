-- Package 19: json.tarkov.dev now reuses one objective id for the same underlying objective
-- shared by several tasks (a "find quest item" step common to a quest chain, seen live on
-- 2026-09-17 with '6391d9ba4b15ca31f76bc325' on three separate tasks). task_objectives and
-- task_objective_items keyed on the objective id alone, so INSERT OR REPLACE silently kept only
-- the last of those tasks' rows. Re-keying both by (task_id, id) stores every task's copy; the
-- validator that used to refuse the whole tasks endpoint over this now only requires the id to
-- be unique within its own task, which is what upstream still guarantees.

CREATE TABLE task_objectives_v2 (
    task_id TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
    id TEXT NOT NULL,
    type TEXT NOT NULL,
    description TEXT NOT NULL,
    map_id TEXT,
    zone_json TEXT,
    PRIMARY KEY (task_id, id)
);

INSERT INTO task_objectives_v2(task_id, id, type, description, map_id, zone_json)
SELECT task_id, id, type, description, map_id, zone_json FROM task_objectives;

DROP TABLE task_objectives;
ALTER TABLE task_objectives_v2 RENAME TO task_objectives;

CREATE TABLE task_objective_items_v2 (
    task_id TEXT NOT NULL,
    objective_id TEXT NOT NULL,
    item_id TEXT NOT NULL,
    count INTEGER NOT NULL,
    found_in_raid_required INTEGER NOT NULL,
    PRIMARY KEY (task_id, objective_id, item_id),
    FOREIGN KEY (task_id, objective_id) REFERENCES task_objectives(task_id, id) ON DELETE CASCADE
);

INSERT INTO task_objective_items_v2(task_id, objective_id, item_id, count, found_in_raid_required)
SELECT objective.task_id, item.objective_id, item.item_id, item.count, item.found_in_raid_required
FROM task_objective_items AS item
JOIN task_objectives AS objective ON objective.id = item.objective_id;

DROP TABLE task_objective_items;
ALTER TABLE task_objective_items_v2 RENAME TO task_objective_items;
