using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.UnitTests.V2Raid;

public sealed class RaidCockpitExtractRowsTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ExtractRowsListOfferedFirstThenUnknownThenNotOfferedAndSkipOtherObjects()
    {
        var objects = new[]
        {
            Object("a", MapSceneObjectKind.Extract, "ZB-1011", MapFeatureFaction.Pmc, MapSceneOfferState.NotOffered),
            Object("b", MapSceneObjectKind.Extract, "Old Gas Station", MapFeatureFaction.Scav, MapSceneOfferState.Unknown),
            Object("c", MapSceneObjectKind.Extract, "RUAF Roadblock", MapFeatureFaction.Shared, MapSceneOfferState.Offered),
            Object("d", MapSceneObjectKind.Transit, "Transit to Factory", MapFeatureFaction.Unknown, MapSceneOfferState.Unknown),
            Object("e", MapSceneObjectKind.SpawnArea, "Big Red", MapFeatureFaction.Pmc, MapSceneOfferState.Unknown),
        };

        var rows = RaidCockpitViewModel.BuildExtractRows(objects);

        Assert.Equal(["RUAF Roadblock", "Old Gas Station", "Transit to Factory", "ZB-1011"], rows.Select(row => row.Name));
        Assert.True(rows[0].IsOffered);
        Assert.Equal("Offered", rows[0].OfferLabel);
        Assert.Equal("PMC · Scav", rows[0].Detail);
        Assert.False(rows[1].HasOfferLabel);
        Assert.Equal("Transit", rows[2].Detail);
        Assert.True(rows[3].IsNotOffered);
        Assert.Equal("Not offered", rows[3].OfferLabel);
    }

    [Fact]
    public void ADuplicatedExtractLabelIsListedOnceKeepingItsOfferedCopy()
    {
        var objects = new[]
        {
            Object("a", MapSceneObjectKind.Extract, "Crossroads", MapFeatureFaction.Pmc, MapSceneOfferState.Unknown),
            Object("b", MapSceneObjectKind.Extract, "Crossroads", MapFeatureFaction.Pmc, MapSceneOfferState.Offered),
        };

        var row = Assert.Single(RaidCockpitViewModel.BuildExtractRows(objects));

        Assert.True(row.IsOffered);
    }

    /// <summary>
    /// [Issue 594] "Clicking an extract or transit doesn't tell me what one it is." Pressing a row
    /// is what selects and centres the extract's own marker on the map; this is the row's half of
    /// that wiring, without a live map or a click.
    /// </summary>
    [Fact]
    public void A_rows_SelectCommand_reports_its_own_scene_id_position_and_name()
    {
        var objects = new[]
        {
            Object("ruaf", MapSceneObjectKind.Extract, "RUAF Roadblock", MapFeatureFaction.Shared, MapSceneOfferState.Offered),
        };
        (MapSceneObjectId Id, MapScenePoint Point, string Name)? selected = null;

        var row = Assert.Single(RaidCockpitViewModel.BuildExtractRows(objects, (id, point, name) => selected = (id, point, name)));
        row.SelectCommand.Execute(null);

        Assert.NotNull(selected);
        Assert.Equal(new MapSceneObjectId("ruaf"), selected!.Value.Id);
        Assert.Equal(new MapScenePoint(1, 1), selected.Value.Point);
        Assert.Equal("RUAF Roadblock", selected.Value.Name);
    }

    /// <summary>A row with no select callback (the direct-coverage tests above) still has a
    /// harmless command rather than none, so nothing bound to it throws.</summary>
    [Fact]
    public void A_rows_SelectCommand_is_never_null_even_without_a_callback()
    {
        var objects = new[] { Object("a", MapSceneObjectKind.Transit, "Transit to Factory", MapFeatureFaction.Unknown, MapSceneOfferState.Unknown) };

        var row = Assert.Single(RaidCockpitViewModel.BuildExtractRows(objects));

        Assert.NotNull(row.SelectCommand);
        row.SelectCommand.Execute(null);
    }

    [Fact]
    public void Requirement_icons_have_the_same_plain_words_as_the_selected_extract()
    {
        var power = new MapSwitch("power", "D-2 Power Switch", "Open", new(1, 2, 3), null, []);
        var requirements = new MapExtractRequirements([power], null, false, false);
        var row = Assert.Single(RaidCockpitViewModel.BuildExtractRows([
            Object("d2", MapSceneObjectKind.Extract, "D-2", MapFeatureFaction.Pmc, MapSceneOfferState.Unknown, requirements),
        ]));

        Assert.True(row.HasRequirements);
        Assert.True(row.NeedsSwitch);
        Assert.Equal("Needs power: D-2 Power Switch", row.RequirementText);
    }

    [Fact]
    public void Reviewed_condition_chips_and_tooltip_text_survive_the_scene_boundary()
    {
        var requirements = new MapExtractRequirements([], null, false, false)
        {
            Conditions =
            [
                new(MapExtractConditionKind.NoArmor, [], null),
                new(MapExtractConditionKind.Items, ["Red Rebel ice pick", "Paracord"], null),
            ],
        };
        var row = Assert.Single(RaidCockpitViewModel.BuildExtractRows([
            Object("cliff", MapSceneObjectKind.Extract, "Cliff Descent", MapFeatureFaction.Pmc, MapSceneOfferState.Unknown, requirements),
        ]));

        Assert.True(row.NeedsNoArmor);
        Assert.True(row.NeedsItems);
        Assert.Equal("No armored vest · Bring Red Rebel ice pick + Paracord", row.RequirementText);
    }

    [Theory]
    [InlineData(null, "Train arrives with 16–12 min left, stays 7 min")]
    [InlineData("0:19:00", "Train in ~3 min, stays 7 min")]
    [InlineData("0:14:00", "Train arriving now–~2 min, stays 7 min")]
    [InlineData("0:10:00", "Train here, up to 5 min left")]
    [InlineData("0:08:00", "Train may still be here, up to 3 min left")]
    [InlineData("0:04:00", "Train has left")]
    public void Timed_window_relates_the_train_schedule_to_known_time_left(string? timeLeft, string expected)
    {
        var window = new MapExtractTimedWindow(
            TimeSpan.FromMinutes(16),
            TimeSpan.FromMinutes(12),
            TimeSpan.FromMinutes(7));

        Assert.Equal(expected, MapExtractRequirementText.DescribeTimedWindow("Armored Train", window, timeLeft));
    }

    [Fact]
    public void A_timed_row_updates_its_tooltip_without_rebuilding_the_extract_list()
    {
        var requirements = new MapExtractRequirements([], null, false, false)
        {
            Conditions =
            [
                new(
                    MapExtractConditionKind.TimedWindow,
                    [],
                    new(TimeSpan.FromMinutes(16), TimeSpan.FromMinutes(12), TimeSpan.FromMinutes(7))),
            ],
        };
        var row = Assert.Single(RaidCockpitViewModel.BuildExtractRows([
            Object("train", MapSceneObjectKind.Extract, "Armored Train", MapFeatureFaction.Shared, MapSceneOfferState.Unknown, requirements),
        ], timeLeft: "0:19:00"));

        Assert.True(row.HasTimedWindow);
        Assert.Equal("Train in ~3 min, stays 7 min", row.RequirementText);

        row.UpdateTimeLeft("0:10:00");

        Assert.Equal("Train here, up to 5 min left", row.RequirementText);
    }

    [Fact]
    public void Rows_built_again_from_the_same_extracts_read_the_same_and_a_changed_offer_does_not()
    {
        // [#453] The cockpit keeps the list it shows when a rebuild's rows read the same, so the
        // panel does not rebuild every row's controls three times a second.
        MapSceneObject[] Objects(MapSceneOfferState ruaf) =>
        [
            Object("a", MapSceneObjectKind.Extract, "ZB-1011", MapFeatureFaction.Pmc, MapSceneOfferState.NotOffered),
            Object("c", MapSceneObjectKind.Extract, "RUAF Roadblock", MapFeatureFaction.Shared, ruaf),
        ];

        var shown = RaidCockpitViewModel.BuildExtractRows(Objects(MapSceneOfferState.Unknown));

        Assert.True(RaidExtractRowViewModel.ReadSame(shown, RaidCockpitViewModel.BuildExtractRows(Objects(MapSceneOfferState.Unknown))));
        Assert.False(RaidExtractRowViewModel.ReadSame(shown, RaidCockpitViewModel.BuildExtractRows(Objects(MapSceneOfferState.Offered))));
        Assert.False(RaidExtractRowViewModel.ReadSame(shown, [shown[0]]));
    }

    private static MapSceneObject Object(
        string id,
        MapSceneObjectKind kind,
        string label,
        MapFeatureFaction faction,
        MapSceneOfferState offerState,
        MapExtractRequirements? requirements = null) => new(
        new(id),
        new("extracts"),
        kind,
        MapSceneTruthKind.StaticReference,
        label,
        null,
        MapSceneGeometry.At(new(1, 1)),
        [],
        new DataProvenance("test", NowUtc),
        faction: faction,
        offerState: offerState,
        extractRequirements: requirements);
}
