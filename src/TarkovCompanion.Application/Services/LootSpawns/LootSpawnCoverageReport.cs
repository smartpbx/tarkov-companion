namespace TarkovCompanion.Application.Services.LootSpawns;

/// <summary>One map's loot-spawn coverage, as the last verified import measured it.</summary>
/// <param name="MapId">The map's slug.</param>
/// <param name="Known">Records the source holds for the map.</param>
/// <param name="Published">Of those, the ones the layer can publish at all.</param>
/// <param name="Positioned">Of the published ones, the ones with a position on the map.</param>
/// <param name="FloorResolved">Of the positioned ones, the ones whose floor is known.</param>
/// <param name="Unresolved">The published ones with no position: listed, never drawn.</param>
public sealed record LootSpawnMapCoverageRow(
    string MapId,
    int Known,
    int Published,
    int Positioned,
    int FloorResolved,
    int Unresolved)
{
    /// <summary>Records the source holds that the layer leaves out entirely.</summary>
    public int LeftOut => Known - Published;
}

/// <summary>
/// The per-map coverage the loot-spawn import already computes, read back as a report.
/// </summary>
/// <remarks>
/// [Issue 318] "Cover every supported map with a published numerator and denominator" was met at
/// import time and then kept nowhere a player could read it: only the map on screen had a line, and
/// only in a panel the Raid page hides. These are the last verified import's own numbers, unchanged,
/// so a map with thin coverage is a figure in Setup rather than a layer that is quietly sparse.
/// </remarks>
public static class LootSpawnCoverageReport
{
    public static IReadOnlyList<LootSpawnMapCoverageRow> From(LootSpawnSourceBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        return [.. bundle.Coverage
            .Select(coverage => new LootSpawnMapCoverageRow(
                coverage.MapId,
                coverage.KnownRecordCount,
                coverage.PublishedRecordCount,
                coverage.PositionedRecordCount,
                coverage.FloorResolvedRecordCount,
                coverage.UnresolvedRecordCount))
            .OrderBy(row => row.MapId, StringComparer.OrdinalIgnoreCase)];
    }
}
