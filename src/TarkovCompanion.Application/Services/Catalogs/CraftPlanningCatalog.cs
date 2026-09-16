namespace TarkovCompanion.Application.Services.Catalogs;

/// <summary>One item consumed or produced by a craft.</summary>
/// <param name="ItemId">
/// The upstream item id. The legacy normalized requirement column is nullable, so its absence
/// stays explicit instead of becoming an empty identifier; current normalized outputs state it.
/// </param>
/// <param name="Count">The stated quantity. Null preserves an absent legacy value.</param>
/// <param name="SourceJson">The exact source fragment retained for forward compatibility.</param>
public sealed record CraftItemQuantity(string? ItemId, decimal? Count, string SourceJson);

/// <summary>One recorded cost/yield observation for a craft.</summary>
/// <remarks>
/// Cost and yield are independent observations. Either may be unknown, and consumers must not
/// turn that absence into zero when ranking a craft.
/// </remarks>
public sealed record CraftEconomicsObservation(
    Guid HistoryId,
    string? StationId,
    int? StationLevel,
    DateTimeOffset? ObservedUtc,
    DateTimeOffset RecordedUtc,
    string? OutputItemId,
    double? OutputCount,
    long? EstimatedCostRoubles,
    long? EstimatedYieldRoubles,
    string Source,
    string PayloadJson);

/// <summary>A synced craft and the bounded history available for planning it.</summary>
public sealed record CraftPlanningEntry(
    string CraftId,
    string? StationId,
    int? StationLevel,
    string SourceJson,
    IReadOnlyList<CraftItemQuantity> Requirements,
    IReadOnlyList<CraftItemQuantity> Outputs,
    IReadOnlyList<CraftEconomicsObservation> History);

/// <summary>Reads the normalized craft catalog through planning-shaped queries.</summary>
public interface ICraftPlanningCatalog
{
    const int DefaultHistoryLimitPerCraft = 20;
    const int MaximumHistoryLimitPerCraft = 100;

    /// <summary>Returns every craft for one exact station id.</summary>
    Task<IReadOnlyList<CraftPlanningEntry>> GetByStationAsync(
        string stationId,
        int historyLimitPerCraft,
        CancellationToken cancellationToken);

    /// <summary>Returns one exact craft id, if it is present in the latest normalized catalog.</summary>
    Task<CraftPlanningEntry?> GetByCraftAsync(
        string craftId,
        int historyLimit,
        CancellationToken cancellationToken);
}
