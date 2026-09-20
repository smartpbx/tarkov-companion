-- 0013 dropped task_objectives while foreign keys were on, and the old task_objective_items
-- cascaded on delete, so its copy step read an empty table: every upgraded database lost all of
-- its quest item needs (15,177 rows on the 2026-09-14 catalog) until the next tasks sync.
-- quest_objective_item_targets holds the same facts and was not touched, so the rows are rebuilt
-- from it exactly as RefreshTasksAsync writes them: only non-failure 'items' targets, the count
-- and found-in-raid flag taken from the objective, one row per (task, objective, item), and only
-- for the snapshot the legacy task tables were last filled from. A table that already has rows
-- was filled by a sync after 0013 and is left alone.

INSERT OR IGNORE INTO task_objective_items(task_id, objective_id, item_id, count, found_in_raid_required)
SELECT
    target.task_id,
    target.objective_id,
    target.item_id,
    COALESCE(objective.target_count, 1),
    CASE WHEN objective.found_in_raid_required = 1 THEN 1 ELSE 0 END
FROM quest_objective_item_targets AS target
JOIN quest_catalog_objectives AS objective
    ON objective.source_key = target.source_key
   AND objective.source_mode = target.source_mode
   AND objective.language = target.language
   AND objective.task_id = target.task_id
   AND objective.id = target.objective_id
   AND objective.is_failure_condition = target.is_failure_condition
JOIN task_objectives AS legacy
    ON legacy.task_id = target.task_id
   AND legacy.id = target.objective_id
WHERE target.is_failure_condition = 0
  AND target.source_field = 'items'
  AND NOT EXISTS (SELECT 1 FROM task_objective_items)
  AND (target.source_key, target.source_mode, target.language) = (
        SELECT source_key, source_mode, language
        FROM quest_catalog_snapshots
        ORDER BY validated_utc DESC, fetched_utc DESC
        LIMIT 1)
GROUP BY target.task_id, target.objective_id, target.item_id;
