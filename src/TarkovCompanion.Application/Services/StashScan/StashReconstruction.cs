using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Stash;

namespace TarkovCompanion.Application.Services.StashScan;

/// <summary>One rectangle of the reconstructed stash: a named item, or a place something is.</summary>
public sealed record StashReconstructedTile(
    string ContainerPath,
    int Row,
    int Column,
    int Width,
    int Height,
    string? ItemId,
    string? DisplayName,
    int? Quantity,
    IReadOnlyList<string> CandidateNames,
    EvidenceProvenance Provenance)
{
    public bool IsKnown => ItemId is not null;

    /// <summary>The key review commands address a tile by: its container and absolute anchor.</summary>
    public string ItemKey => $"{ContainerPath}@{Row}:{Column}";
}

/// <summary>One container's grid, in the container's own coordinates rather than any screenshot's.</summary>
public sealed record StashReconstructedContainer(
    string ContainerPath,
    int Rows,
    int Columns,
    IReadOnlyList<StashReconstructedTile> Tiles);

/// <summary>The stash as one picture: every placed screenshot folded together, overlap counted once.</summary>
public sealed record StashReconstruction(
    IReadOnlyList<StashReconstructedContainer> Containers,
    int UnplacedRegions,
    int KnownTiles,
    int UnknownTiles,
    IReadOnlyDictionary<string, int> OwnedCounts)
{
    public static StashReconstruction Empty { get; } = new([], 0, 0, 0, new Dictionary<string, int>(StringComparer.Ordinal));
}

/// <summary>
/// Folds a scan's overlapping screenshots into the one grid the player would recognise.
/// </summary>
/// <remarks>
/// <para>
/// A snapshot stores what each screenshot showed, and the workspace used to list it that way: one
/// grid per screenshot, every item in a shared row listed twice. Measured on a painted 34-row stash
/// scanned in three overlapping screens (package 40): summing the regions reported 36 items the
/// stash does not contain. It also skipped every cell without a name, so an unread cell was not
/// shown as unknown - it was not shown.
/// </para>
/// <para>
/// Here every placed region is moved to container coordinates and each absolute cell is claimed
/// once. A named tile beats an unnamed one for the same cells, and a whole tile beats one that
/// touches the top or bottom edge of its own screenshot, because the viewport cuts items there and
/// the cut half reads as a smaller, unknown thing. Two different names for the same anchor are not
/// chosen between: the tile stays, as unknown.
/// </para>
/// <para>
/// Counts are what was seen, so they are lower bounds: a stack whose number was not read counts
/// as one, and nothing inside a closed container counts at all.
/// </para>
/// </remarks>
public sealed class StashReconstructionProjector
{
    public StashReconstruction Project(StashRecognition stash)
    {
        ArgumentNullException.ThrowIfNull(stash);
        var unplaced = 0;
        var candidates = new List<Candidate>();
        var columnsByContainer = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var region in stash.CapturedRegions.OrderBy(region => region.CaptureOrdinal))
        {
            if (region.OriginInContainer.Value is not { } origin ||
                region.OriginInContainer.Status.Completeness is ResultCompleteness.Unknown or ResultCompleteness.Unavailable)
            {
                unplaced++;
                continue;
            }

            var regionRows = region.Grid.Geometry.Rows.Value;
            var regionColumns = region.Grid.Geometry.Columns.Value ?? 0;
            columnsByContainer[region.ContainerPath] = Math.Max(
                columnsByContainer.GetValueOrDefault(region.ContainerPath),
                origin.Column + regionColumns);
            foreach (var cell in region.Grid.Cells)
            {
                var (width, height) = StashFootprints.Of(region.Grid, cell);
                var item = cell.Item.Value;
                var itemId = string.IsNullOrWhiteSpace(item?.CanonicalId.Value) ? null : item!.CanonicalId.Value!.Trim();
                var touchesEdge = regionRows is { } rows &&
                                  ((cell.Anchor.Row == 0 && origin.Row > 0) || cell.Anchor.Row + height >= rows);
                candidates.Add(new(
                    new StashReconstructedTile(
                        region.ContainerPath,
                        origin.Row + cell.Anchor.Row,
                        origin.Column + cell.Anchor.Column,
                        width,
                        height,
                        itemId,
                        itemId is null ? null : item!.DisplayName.Value ?? itemId,
                        itemId is null ? null : item!.Quantity.Value,
                        cell.Item.Candidates
                            .Select(candidate => candidate.Value.DisplayName.Value ?? candidate.Value.CanonicalId.Value)
                            .Where(name => !string.IsNullOrWhiteSpace(name))
                            .Select(name => name!)
                            .Distinct(StringComparer.Ordinal)
                            .Take(3)
                            .ToArray(),
                        cell.Item.Provenance),
                    touchesEdge,
                    region.CaptureOrdinal));
            }
        }

        var claimed = new Dictionary<(string Container, int Row, int Column), int>();
        var placed = new List<StashReconstructedTile?>();
        foreach (var candidate in candidates
                     .OrderBy(candidate => candidate.Tile.IsKnown ? 0 : 1)
                     .ThenBy(candidate => candidate.TouchesEdge ? 1 : 0)
                     .ThenBy(candidate => candidate.CaptureOrdinal)
                     .ThenBy(candidate => candidate.Tile.Row)
                     .ThenBy(candidate => candidate.Tile.Column))
        {
            var tile = candidate.Tile;
            var owners = Cells(tile)
                .Select(cell => claimed.TryGetValue(cell, out var index) ? index : -1)
                .Where(index => index >= 0)
                .Distinct()
                .ToArray();
            if (owners.Length == 0)
            {
                foreach (var cell in Cells(tile))
                {
                    claimed[cell] = placed.Count;
                }

                placed.Add(tile);
                continue;
            }

            // The same anchor named two ways: neither reading is chosen.
            if (owners.Length == 1 && placed[owners[0]] is { IsKnown: true } existing && tile.IsKnown &&
                existing.Row == tile.Row && existing.Column == tile.Column &&
                !candidate.TouchesEdge &&
                !string.Equals(existing.ItemId, tile.ItemId, StringComparison.Ordinal))
            {
                placed[owners[0]] = existing with
                {
                    ItemId = null,
                    DisplayName = null,
                    Quantity = null,
                    CandidateNames = new[] { existing.DisplayName, tile.DisplayName }
                        .Where(name => name is not null)
                        .Select(name => name!)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray(),
                };
            }
        }

        var tiles = placed.Where(tile => tile is not null).Select(tile => tile!).ToArray();
        var containers = tiles
            .GroupBy(tile => tile.ContainerPath, StringComparer.Ordinal)
            .OrderBy(group => group.Key.Count(character => character == '/'))
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new StashReconstructedContainer(
                group.Key,
                group.Max(tile => tile.Row + tile.Height),
                Math.Max(columnsByContainer.GetValueOrDefault(group.Key), group.Max(tile => tile.Column + tile.Width)),
                group.OrderBy(tile => tile.Row).ThenBy(tile => tile.Column).ToArray()))
            .ToArray();
        var owned = tiles
            .Where(tile => tile.IsKnown)
            .GroupBy(tile => tile.ItemId!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Aggregate(0, (total, tile) => (int)Math.Min(int.MaxValue, (long)total + Math.Max(1, tile.Quantity ?? 1))),
                StringComparer.Ordinal);
        return new(
            containers,
            unplaced,
            tiles.Count(tile => tile.IsKnown),
            tiles.Count(tile => !tile.IsKnown),
            owned);
    }

    private static IEnumerable<(string Container, int Row, int Column)> Cells(StashReconstructedTile tile)
    {
        for (var row = 0; row < tile.Height; row++)
        {
            for (var column = 0; column < tile.Width; column++)
            {
                yield return (tile.ContainerPath, tile.Row + row, tile.Column + column);
            }
        }
    }

    private sealed record Candidate(StashReconstructedTile Tile, bool TouchesEdge, int CaptureOrdinal);
}
