namespace TarkovCompanion.Application.Services.StashScan;

/// <summary>
/// Combines complementary snapshots for review without mutating either one. The selected
/// snapshot wins every occupied square; an older snapshot only fills squares it did not cover.
/// </summary>
public sealed class StashReconstructionMerger
{
    public StashReconstruction Merge(StashReconstruction selected, IEnumerable<StashReconstruction> additions)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(additions);
        var additional = additions.ToArray();
        var tiles = selected.Containers.SelectMany(container => container.Tiles).ToList();
        var occupied = tiles.SelectMany(Cells).ToHashSet();
        var unplaced = selected.UnplacedRegions;

        foreach (var addition in additional)
        {
            ArgumentNullException.ThrowIfNull(addition);
            unplaced += addition.UnplacedRegions;
            foreach (var tile in addition.Containers.SelectMany(container => container.Tiles))
            {
                var cells = Cells(tile).ToArray();
                if (cells.Any(occupied.Contains))
                {
                    continue;
                }

                tiles.Add(tile);
                occupied.UnionWith(cells);
            }
        }

        var dimensions = selected.Containers
            .Concat(additional.SelectMany(addition => addition.Containers))
            .GroupBy(container => container.ContainerPath, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (Rows: group.Max(container => container.Rows), Columns: group.Max(container => container.Columns)),
                StringComparer.Ordinal);
        var containers = tiles
            .GroupBy(tile => tile.ContainerPath, StringComparer.Ordinal)
            .OrderBy(group => group.Key.Count(character => character == '/'))
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new StashReconstructedContainer(
                group.Key,
                Math.Max(dimensions[group.Key].Rows, group.Max(tile => tile.Row + tile.Height)),
                Math.Max(dimensions[group.Key].Columns, group.Max(tile => tile.Column + tile.Width)),
                group.OrderBy(tile => tile.Row).ThenBy(tile => tile.Column).ToArray()))
            .ToArray();
        var owned = tiles
            .Where(tile => tile.IsKnown)
            .GroupBy(tile => tile.ItemId!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Aggregate(0, (total, tile) =>
                    (int)Math.Min(int.MaxValue, (long)total + Math.Max(1, tile.Quantity ?? 1))),
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
}
