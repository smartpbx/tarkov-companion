namespace TarkovCompanion.Application.Services.StashScan;

/// <summary>
/// The player's identity and count corrections laid over a reconstructed stash (#712 1-12).
/// </summary>
/// <remarks>
/// A correction used to be saved to the review log and listed as "pending": nothing applied it,
/// so the corrected tile went on showing the old name or none, and the sort plan and holdings
/// went on using it. The log is still the record and the snapshot's evidence is untouched; this
/// is the view the page, the plan and the counts are built from.
/// </remarks>
public static class StashReviewCorrections
{
    /// <summary>What a correction to "not an item I can name" is stored as.</summary>
    public const string Unknown = "unknown";

    public static StashReconstruction Apply(StashReconstruction reconstruction, StashReviewCommandState state)
    {
        ArgumentNullException.ThrowIfNull(reconstruction);
        ArgumentNullException.ThrowIfNull(state);
        if (state.CorrectedIdentities.Count == 0 && state.CorrectedQuantities.Count == 0)
        {
            return reconstruction;
        }

        var containers = reconstruction.Containers
            .Select(container => container with { Tiles = [.. container.Tiles.Select(tile => Apply(tile, state))] })
            .ToArray();
        var tiles = containers.SelectMany(container => container.Tiles).ToArray();
        var owned = tiles
            .Where(tile => tile.IsKnown)
            .GroupBy(tile => tile.ItemId!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Aggregate(0, (total, tile) => (int)Math.Min(int.MaxValue, (long)total + Math.Max(1, tile.Quantity ?? 1))),
                StringComparer.Ordinal);
        return reconstruction with
        {
            Containers = containers,
            KnownTiles = tiles.Count(tile => tile.IsKnown),
            UnknownTiles = tiles.Count(tile => !tile.IsKnown),
            OwnedCounts = owned,
        };
    }

    private static StashReconstructedTile Apply(StashReconstructedTile tile, StashReviewCommandState state)
    {
        var corrected = tile;
        if (state.CorrectedIdentities.TryGetValue(tile.ItemKey, out var itemId) &&
            !string.Equals(itemId, tile.ItemId, StringComparison.Ordinal))
        {
            // The name is looked up again from the catalog by the page; the read name belonged
            // to the item it no longer is.
            corrected = string.Equals(itemId, Unknown, StringComparison.OrdinalIgnoreCase)
                ? corrected with { ItemId = null, DisplayName = null, Quantity = null }
                : corrected with { ItemId = itemId, DisplayName = null };
        }

        if (state.CorrectedQuantities.TryGetValue(tile.ItemKey, out var quantity) && corrected.IsKnown)
        {
            corrected = corrected with { Quantity = quantity };
        }

        return corrected;
    }
}
