using TarkovCompanion.App.ViewModels.Maps;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// What the column beside the map says there is to do on it.
/// </summary>
/// <remarks>
/// The map has drawn quest objectives for a while, and the only list of them was a flat
/// per-objective dump in the expander at the bottom left, which is where the map explains
/// itself rather than where a player looks.
/// </remarks>
public sealed class MapQuestPanelTests
{
    [Fact]
    public void OneRowPerQuestRatherThanPerObjective()
    {
        var rows = MapViewModel.SummarizeQuestsForTest([
            Objective("Debut", "Kill five Scavs on Customs"),
            Objective("Debut", "Hand over two MP-133 shotguns"),
            Objective("Shootout Picnic", "Eliminate the Scav raiders"),
        ]);

        Assert.Equal(2, rows.Count);
        var debut = Assert.Single(rows, row => row.Task == "Debut");
        Assert.Contains("Kill five Scavs on Customs", debut.Objectives, StringComparison.Ordinal);
        Assert.Contains("Hand over two MP-133 shotguns", debut.Objectives, StringComparison.Ordinal);
    }

    /// <summary>
    /// A pinned quest goes first, because that is the player saying which one they are doing.
    /// </summary>
    [Fact]
    public void PinnedComesFirst()
    {
        var rows = MapViewModel.SummarizeQuestsForTest([
            Objective("Acquaintance", "Find the letter"),
            Objective("Zhivchik", "Deliver the flash drive", isPinned: true),
        ]);

        Assert.Equal("Zhivchik", rows[0].Task);
        Assert.True(rows[0].IsPinned);
        Assert.False(rows[1].IsPinned);
    }

    /// <summary>
    /// An exact point is somewhere to walk to; an association is only a claim about the map.
    /// </summary>
    [Fact]
    public void AQuestPlacedOnlyByAssociationSaysSo()
    {
        var rows = MapViewModel.SummarizeQuestsForTest([Objective("Debut", "Kill five Scavs")]);

        Assert.True(Assert.Single(rows).IsApproximate);
    }

    [Fact]
    public void AQuestWithAPlacedObjectiveDoesNot()
    {
        var rows = MapViewModel.SummarizeQuestsForTest([
            Objective("Debut", "Kill five Scavs"),
            Objective("Debut", "Mark the car", placed: true),
        ]);

        Assert.False(Assert.Single(rows).IsApproximate);
    }

    [Fact]
    public void NothingOnThisMapIsAnEmptyList() =>
        Assert.Empty(MapViewModel.SummarizeQuestsForTest([]));

    private static QuestMapObjectiveProjection Objective(
        string task,
        string description,
        bool isPinned = false,
        bool placed = false) => new(
        task.ToLowerInvariant(),
        task,
        description.ToLowerInvariant(),
        description,
        QuestObjectiveKind.Mark,
        false,
        isPinned,
        null,
        placed ? QuestMapGeometryKind.Point : QuestMapGeometryKind.AssociationOnly,
        placed ? [new MapPoint(10, 20)] : [],
        false,
        "fixture",
        null,
        null,
        [],
        new(
            "fixture",
            "https://fixture.invalid",
            GameMode.Regular,
            "regular",
            "en",
            "sha",
            "sha",
            null,
            null,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch),
        new(
            new Uri("https://fixture.invalid"),
            DateTimeOffset.UnixEpoch,
            "sha",
            MapCatalogAvailability.Cached),
        "fixture");
}
