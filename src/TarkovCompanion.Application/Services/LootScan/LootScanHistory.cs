using TarkovCompanion.Core.Domain.Loot;

namespace TarkovCompanion.Application.Services.LootScan;

/// <summary>One call of a saved Loot Scan, as the player was shown it. No pixels.</summary>
/// <param name="ItemId">The catalog id, when the item was named.</param>
/// <param name="Name">What the row was headed with.</param>
/// <param name="Placement">"Place in rig, row 1, column 1", or empty.</param>
/// <param name="ValueRoubles">The net value the engine settled, else the catalog's figure.</param>
/// <param name="Confidence">How sure the identity was, 0..1, when scored.</param>
/// <param name="Reason">The row's headline reason.</param>
public sealed record SavedLootScanItem(
    string? ItemId,
    string Name,
    LootScanVerdict Verdict,
    string Placement,
    long? ValueRoubles,
    double? Confidence,
    string Reason);

/// <summary>
/// A completed Loot Scan kept after the page moved on (#274, #282, #291). Before this only stash
/// snapshots carried the recommendation ruleset version, so nothing could say which rules made an
/// old loot call.
/// </summary>
public sealed record SavedLootScan(
    string ScanId,
    Guid? RaidId,
    string? MapId,
    DateTimeOffset EvaluatedUtc,
    string RulesetVersion,
    bool IsComplete,
    IReadOnlyList<SavedLootScanItem> Items)
{
    public int Count(LootScanVerdict verdict) => Items.Count(item => item.Verdict == verdict);

    /// <summary>What the takes and swaps were worth, where a value was known.</summary>
    public long TakenValueRoubles => Items
        .Where(item => item.Verdict is LootScanVerdict.Take or LootScanVerdict.Swap)
        .Sum(item => item.ValueRoubles ?? 0);

    /// <summary>The id a Debrief correction ("Wrong") names this scan by, apart from raid events.</summary>
    public string CorrectionId => CorrectionPrefix + ScanId;

    public const string CorrectionPrefix = "loot:";
}

public interface ILootScanHistoryStore
{
    /// <summary>Saves a scan, replacing an earlier save of the same scan id, then applies the retention bound.</summary>
    Task SaveAsync(SavedLootScan scan, CancellationToken cancellationToken);

    /// <summary>A raid's scans, oldest first.</summary>
    Task<IReadOnlyList<SavedLootScan>> ListForRaidAsync(Guid raidId, CancellationToken cancellationToken);

    /// <summary>The newest scans, newest first.</summary>
    Task<IReadOnlyList<SavedLootScan>> ListRecentAsync(int limit, CancellationToken cancellationToken);
}

/// <summary>
/// The bound on saved loot scans. The store applies it on every save because the maintenance
/// prune is off until the player sets a retention period (0011), and a scan a minute over a
/// long session would otherwise grow the table without limit.
/// </summary>
public static class LootScanHistoryRetention
{
    public const int MaximumScans = 500;

    public static TimeSpan MaximumAge { get; } = TimeSpan.FromDays(90);

    public const int MaximumItemsPerScan = LootScanPlannerLimits.MaximumVisibleItems;
}
