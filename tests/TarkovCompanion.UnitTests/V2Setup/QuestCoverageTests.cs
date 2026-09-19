using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.App.ViewModels.V2.Setup;
using TarkovCompanion.App.ViewModels.V2.Shell;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.UnitTests.V2Setup;

/// <summary>Issue 379: the per-map report of which quest objectives the quest data can place.</summary>
public sealed class QuestCoverageTests
{
    private static readonly QuestMapAssociation OnCustoms = new(QuestMapAssociationKind.Declared, 0, "game-customs");

    [Fact]
    public void EveryObjectiveIsCountedOnceUnderItsMapAsPlacedCandidatesOnlyNoPlaceOrPlacedByThePlayer()
    {
        var catalog = Catalog(
            Objective("placed", [Zone(position: new(1, 2, 3))], OnCustoms),
            Objective("area", [Zone(outline: [new(0, 0, 0), new(1, 0, 0), new(1, 1, 0)])], OnCustoms),
            Objective("candidates", [PossibleLocation(), PossibleLocation()], OnCustoms),
            Objective("none", [], OnCustoms),
            Objective("mine", [], OnCustoms),
            Objective("empty-zone", [Zone()], OnCustoms),
            // No map at all (hand in an item) is not a coverage gap, and a failure condition is
            // not something to go and do.
            Objective("no-map", []));
        var marker = new UserQuestMarker("mine", "customs", null, 5, 5, DateTimeOffset.UnixEpoch);

        var row = Assert.Single(QuestObjectiveCoverage.Measure(catalog, [marker], id => id == "game-customs" ? "customs" : null));

        Assert.Equal("customs", row.MapId);
        Assert.Equal(new QuestMapCoverage("customs", 6, 2, 1, 2, 1), row);
        Assert.Equal(4, row.Drawable);
    }

    [Fact]
    public void AnObjectiveNamingTheSameMapTwiceUnderItsTwoIdsIsCountedOnce()
    {
        var both = Objective(
            "twice",
            [],
            OnCustoms,
            new QuestMapAssociation(QuestMapAssociationKind.Zone, 1, "customs"));

        var row = Assert.Single(QuestObjectiveCoverage.Measure(
            Catalog(both),
            [],
            id => id is "game-customs" or "customs" ? "customs" : null));

        Assert.Equal(1, row.Objectives);
    }

    [Fact]
    public void AMapTheAppDoesNotKnowIsStillCountedUnderTheCatalogsOwnId()
    {
        var row = Assert.Single(QuestObjectiveCoverage.Measure(
            Catalog(Objective("x", [], new QuestMapAssociation(QuestMapAssociationKind.Declared, 0, "unknown-map"))),
            [],
            _ => null));

        Assert.Equal("unknown-map", row.MapId);
    }

    [Fact]
    public void TheSummaryLeavesOutWhatIsZero()
    {
        Assert.Equal("96 of 120 placed", QuestCoverageViewModel.Summarize(new("customs", 120, 96, 0, 24, 0)).Split(" · ")[0]);
        Assert.Equal("96 of 120 placed", QuestCoverageViewModel.Summarize(new("customs", 120, 96, 0, 0, 0)));
        Assert.Equal(
            "96 of 120 placed · 21 with no place · 3 placed by you",
            QuestCoverageViewModel.Summarize(new("customs", 120, 90, 6, 21, 3)));
    }

    [Fact]
    public async Task TheApplicationCompositionOffersTheMarkerStoreTheCockpitAndTheSetupReport()
    {
        // Fails if the store, the cockpit's use of it or Setup's report is not composed.
        var root = Path.Combine(Path.GetTempPath(), $"tarkov-questcoverage-{Guid.NewGuid():N}");
        try
        {
            await using var services = AppComposition.Build(
                new AppCommandLine(false, true, false, false, null, null, null) { UiShell = V2ShellMode.VariantA },
                new(DataRoot: root, Offline: true));

            Assert.NotNull(services.GetRequiredService<IUserQuestMarkStore>());
            Assert.NotNull(services.GetRequiredService<RaidCockpitViewModel>());
            var coverage = services.GetRequiredService<V2ShellViewModel>().SetupWorkspace?.QuestCoverage;
            Assert.NotNull(coverage);
            await coverage.RefreshAsync();
            Assert.False(string.IsNullOrWhiteSpace(coverage.Status));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static QuestObjectiveZone Zone(WorldPosition? position = null, IReadOnlyList<WorldPosition>? outline = null) => new(
        0, "zone", "game-customs", position, outline ?? [], null, null, null, null, null, "{}");

    private static QuestObjectiveZone PossibleLocation() => new(
        0, null, "game-customs", new(1, 1, 1), [], null, null, null, null, QuestObjectiveZone.PossibleLocationName, "{}");

    private static QuestObjectiveDefinition Objective(
        string id,
        IReadOnlyList<QuestObjectiveZone> zones,
        params QuestMapAssociation[] maps) => new(
        id,
        "task",
        "visit",
        QuestObjectiveKind.Unsupported,
        false,
        0,
        id,
        null,
        null,
        null,
        null,
        [],
        [],
        maps,
        zones,
        "{}",
        "{}");

    private static QuestCatalogSnapshot Catalog(params QuestObjectiveDefinition[] objectives) => new(
        new(
            "fixture",
            "https://fixture.invalid/tasks",
            GameMode.Regular,
            "regular",
            "en",
            "hash",
            "translated-hash",
            null,
            null,
            DateTimeOffset.Parse("2026-09-10T10:00:00Z"),
            DateTimeOffset.Parse("2026-09-10T10:00:00Z")),
        [
            new QuestTaskDefinition(
                "task", "Task", null, null, null, null, null, null, null, null, null, null, null,
                ["regular"], [], objectives, [new QuestObjectiveDefinition(
                    "failure", "task", "visit", QuestObjectiveKind.Unsupported, true, 0, "fail", null, null, null, null, [], [], [OnCustoms], [], "{}", "{}")],
                "{}"),
        ],
        "{}",
        "{}");
}
