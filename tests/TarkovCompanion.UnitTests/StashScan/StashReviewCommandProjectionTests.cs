using TarkovCompanion.Application.Services.StashScan;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Stash;

namespace TarkovCompanion.UnitTests.StashScan;

public sealed class StashReviewCommandProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PinIgnoreRescanAndSnapshotMergeAreProjectedAndEachCanBeUndone()
    {
        var current = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var previous = Guid.Parse("10000000-0000-0000-0000-000000000002");
        var pin = Command(StashReviewActionKind.Pin, ["stash@1:1"], 1);
        var ignore = Command(StashReviewActionKind.Ignore, ["stash@2:2"], 2);
        var rescan = Command(StashReviewActionKind.Rescan, ["stash/case"], 3);
        var merge = Command(
            StashReviewActionKind.MergeEntries,
            [current.ToString("D"), previous.ToString("D")],
            4,
            StashReviewCommandProjection.SnapshotMergeOrigin);

        var active = StashReviewCommandProjection.Project([pin, ignore, rescan, merge]);

        Assert.Contains("stash@1:1", active.PinnedItemKeys);
        Assert.Contains("stash@2:2", active.IgnoredItemKeys);
        Assert.Contains("stash/case", active.RescanContainerPaths);
        Assert.Contains(previous, active.MergedSnapshotIds);
        Assert.Equal(merge.CommandId, active.LatestUndoable?.CommandId);

        var commands = new List<StashReviewCommand> { pin, ignore, rescan, merge };
        commands.Add(StashReviewCommandProjection.Undo(merge, Now.AddMinutes(5), Guid.Parse("20000000-0000-0000-0000-000000000001")));
        commands.Add(StashReviewCommandProjection.Undo(rescan, Now.AddMinutes(6), Guid.Parse("20000000-0000-0000-0000-000000000002")));
        commands.Add(StashReviewCommandProjection.Undo(ignore, Now.AddMinutes(7), Guid.Parse("20000000-0000-0000-0000-000000000003")));
        commands.Add(StashReviewCommandProjection.Undo(pin, Now.AddMinutes(8), Guid.Parse("20000000-0000-0000-0000-000000000004")));

        var undone = StashReviewCommandProjection.Project(commands);

        Assert.Empty(undone.PinnedItemKeys);
        Assert.Empty(undone.IgnoredItemKeys);
        Assert.Empty(undone.RescanContainerPaths);
        Assert.Empty(undone.MergedSnapshotIds);
        Assert.Null(undone.LatestUndoable);
    }

    [Fact]
    public void MergeKeepsTheSelectedSnapshotAndFillsOnlyItsUncoveredSquares()
    {
        var selected = Reconstruction(Tile(0, 0, "selected"));
        var addition = Reconstruction(
            Tile(0, 0, "overlap-from-old"),
            Tile(2, 0, "filled-from-old"));

        var merged = new StashReconstructionMerger().Merge(selected, [addition]);

        var tiles = Assert.Single(merged.Containers).Tiles;
        Assert.Equal(2, tiles.Count);
        Assert.Contains(tiles, tile => tile.ItemId == "selected");
        Assert.Contains(tiles, tile => tile.ItemId == "filled-from-old");
        Assert.DoesNotContain(tiles, tile => tile.ItemId == "overlap-from-old");
        Assert.Equal(2, merged.KnownTiles);
    }

    private static StashReviewCommand Command(
        StashReviewActionKind action,
        IReadOnlyList<string> targets,
        int minute,
        string origin = StashReviewCommandProjection.WorkspaceOrigin) => new(
        Guid.Parse($"00000000-0000-0000-0000-{minute:D12}"),
        "recognition-snapshot",
        action,
        targets,
        Now.AddMinutes(minute),
        origin);

    private static StashReconstructedTile Tile(int row, int column, string itemId) => new(
        "stash",
        row,
        column,
        1,
        1,
        itemId,
        itemId,
        1,
        [],
        new EvidenceProvenance(
            EvidenceSourceClass.GameWrittenScreenshot,
            "fixture://stash",
            Now,
            EvidenceConfidence.Certain,
            new ProducerIdentity("fixture", "1")));

    private static StashReconstruction Reconstruction(params StashReconstructedTile[] tiles) => new(
        [new StashReconstructedContainer("stash", 4, 2, tiles)],
        0,
        tiles.Length,
        0,
        tiles.ToDictionary(tile => tile.ItemId!, _ => 1, StringComparer.Ordinal));
}
