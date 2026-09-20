namespace TarkovCompanion.Infrastructure.Persistence;

/// <summary>One permanently reserved migration identifier and its verification rollback fixture.</summary>
public sealed record SqliteMigrationDefinition(string Id, bool IsDestructive, string Purpose)
{
    public string UpgradeResourceSuffix => $".Persistence.Migrations.{Id}.sql";

    public string RollbackResourceSuffix => $".Persistence.Migrations.{Id}.rollback.sql";
}

/// <summary>
/// The only migration sequence. An identifier in this list is reserved forever and is never
/// reused, even when a later migration removes everything it created.
/// </summary>
public static class SqliteMigrationLedger
{
    public static IReadOnlyList<SqliteMigrationDefinition> Entries { get; } =
    [
        new("0001_initial", false, "Initial normalized catalog and local state"),
        new("0002_data_cache", false, "Conditional HTTP cache and item price columns"),
        new("0003_recognition_scan_metadata", false, "Recognition diagnostic metadata"),
        new("0004_quest_catalog_fidelity", false, "Mode-scoped quest source fidelity"),
        new("0005_local_quest_progress", false, "Local quest progress and journal"),
        new("0006_quest_progress_exchange", false, "Reviewed imports, unresolved IDs, and undo"),
        new("0007_drop_superseded_tables", true, "Remove superseded empty v1 tables"),
        new("0008_loot_containers", false, "Normalized loot-container names"),
        new("0009_drop_unread_map_tables", true, "Remove duplicated unread map tables"),
        new("0010_drop_quest_catalog_orphans", true, "Remove superseded quest orphan cache"),
        new("0011_v2_data_platform", true, "Compact, recoverable V2 persistence platform"),
        new("0012_task_wiki_link", false, "Task wiki deep links"),
        new("0013_task_objective_task_scoped_keys", true, "Task-scoped task_objectives/task_objective_items keys"),
        new("0014_quest_progress_game_log_actor", true, "Allow GameLog actor on quest_progress_journal"),
        // 0014 is main's quest-journal actor migration, which landed first. This one was written
        // as 0014 on a branch cut before it; two ids sharing a number sort by their names, and
        // "0014_flea" sorts ahead of "0014_quest", which the ordered-ledger guard refuses.
        new("0015_flea_market_settings", false, "Published flea listing fee rates"),
        new("0016_restore_task_objective_items", false, "Put back the quest item needs 0013 deleted"),
    ];

    public static SqliteMigrationDefinition Get(string id) =>
        Entries.SingleOrDefault(entry => string.Equals(entry.Id, id, StringComparison.Ordinal))
        ?? throw new KeyNotFoundException($"Migration '{id}' is not reserved in the sequence ledger.");
}
