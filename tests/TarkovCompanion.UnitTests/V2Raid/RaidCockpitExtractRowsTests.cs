using TarkovCompanion.Application.Services.Maps;
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

        // The raid's side is not given, so the Scav-only exit comes after the transit (#873).
        Assert.Equal(["RUAF Roadblock", "Transit to Factory", "Old Gas Station", "ZB-1011"], rows.Select(row => row.Name));
        Assert.True(rows[0].IsOffered);
        Assert.Equal("Offered", rows[0].OfferLabel);
        Assert.Equal("PMC · Scav", rows[0].Detail);
        Assert.False(rows[2].HasOfferLabel);
        Assert.Equal("Transit", rows[1].Detail);
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

    /// <summary>
    /// [#873] "It shows both types of extracts, when it should only show the pmc ones or scav ones
    /// depending on your raid." A PMC raid lists PMC, shared and transit exits; a Scav raid the
    /// Scav, shared and transit ones. Decided by the enum, never by the translated side words.
    /// </summary>
    [Theory]
    [InlineData(MapFeatureFaction.Pmc, new[] { "RUAF Roadblock", "Transit to Factory", "Unmarked Gap", "ZB-1011" })]
    [InlineData(MapFeatureFaction.Scav, new[] { "Old Gas Station", "RUAF Roadblock", "Transit to Factory", "Unmarked Gap" })]
    public void A_known_side_lists_only_its_own_shared_and_transit_exits(MapFeatureFaction side, string[] expected)
    {
        var rows = RaidCockpitViewModel.BuildExtractRows(BothSides(), raidSide: side);

        Assert.Equal(expected, rows.Select(row => row.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.All(rows, row => Assert.False(row.IsSideUnsure));
    }

    [Fact]
    public void An_unknown_side_lists_every_exit_with_the_ones_anybody_can_use_first_and_the_one_side_ones_marked()
    {
        var rows = RaidCockpitViewModel.BuildExtractRows(BothSides(), raidSide: MapFeatureFaction.Unknown);

        Assert.Equal(5, rows.Count);
        // Shared, unstated and transit first; the PMC-only and Scav-only exits after them, dimmed.
        Assert.Equal(["RUAF Roadblock", "Unmarked Gap", "Transit to Factory"], rows.Take(3).Select(row => row.Name));
        Assert.Equal(["Old Gas Station", "ZB-1011"], rows.Skip(3).Select(row => row.Name));
        Assert.All(rows.Take(3), row => Assert.False(row.IsSideUnsure));
        Assert.All(rows.Skip(3), row => Assert.True(row.IsSideUnsure));
        Assert.Equal("Scav only", rows[3].Detail);
        Assert.Equal("PMC only", rows[4].Detail);
    }

    [Fact]
    public void An_offered_exit_still_leads_and_the_unsure_mark_survives_a_route_estimate()
    {
        var objects = BothSides().Append(
            Object("g", MapSceneObjectKind.Extract, "Crossroads", MapFeatureFaction.Pmc, MapSceneOfferState.Offered)).ToArray();

        var pmc = RaidCockpitViewModel.BuildExtractRows(objects, raidSide: MapFeatureFaction.Pmc);
        var unknown = RaidCockpitViewModel.BuildExtractRows(objects, raidSide: MapFeatureFaction.Unknown);

        Assert.Equal("Crossroads", pmc[0].Name);
        Assert.Equal("Crossroads", unknown[0].Name);
        var routed = unknown.Single(row => row.Name == "ZB-1011").WithRoute("~2 min", isRouted: true, command: new TarkovCompanion.App.ViewModels.DelegateCommand(() => { }));
        Assert.True(routed.IsSideUnsure);
    }

    [Theory]
    [InlineData("PMC", MapFeatureFaction.Pmc)]
    [InlineData("pmc", MapFeatureFaction.Pmc)]
    [InlineData(" Scav ", MapFeatureFaction.Scav)]
    [InlineData(null, MapFeatureFaction.Unknown)]
    [InlineData("", MapFeatureFaction.Unknown)]
    [InlineData("savage", MapFeatureFaction.Unknown)]
    public void The_raid_side_code_reads_to_the_enum(string? side, MapFeatureFaction expected) =>
        Assert.Equal(expected, RaidExtractSide.Of(side));

    [Theory]
    [InlineData(MapFeatureFaction.Pmc, MapFeatureFaction.Pmc, true)]
    [InlineData(MapFeatureFaction.Scav, MapFeatureFaction.Pmc, false)]
    [InlineData(MapFeatureFaction.Pmc, MapFeatureFaction.Scav, false)]
    [InlineData(MapFeatureFaction.Shared, MapFeatureFaction.Scav, true)]
    [InlineData(MapFeatureFaction.Unknown, MapFeatureFaction.Scav, true)]
    [InlineData(MapFeatureFaction.Scav, MapFeatureFaction.Unknown, true)]
    public void Only_an_exit_stated_for_the_other_side_is_unusable(MapFeatureFaction extract, MapFeatureFaction raid, bool usable) =>
        Assert.Equal(usable, RaidExtractSide.CanUse(extract, raid));

    /// <summary>[#873] What the Raid map's scene keeps: the V1 side rule, applied to V2.</summary>
    [Theory]
    [InlineData(MapOverlayKind.Extracts, MapFeatureFaction.Scav, MapFeatureFaction.Pmc, false)]
    [InlineData(MapOverlayKind.Extracts, MapFeatureFaction.Pmc, MapFeatureFaction.Scav, false)]
    [InlineData(MapOverlayKind.Extracts, MapFeatureFaction.Pmc, MapFeatureFaction.Pmc, true)]
    [InlineData(MapOverlayKind.Extracts, MapFeatureFaction.Shared, MapFeatureFaction.Pmc, true)]
    [InlineData(MapOverlayKind.Extracts, MapFeatureFaction.Scav, MapFeatureFaction.Unknown, true)]
    [InlineData(MapOverlayKind.Spawns, MapFeatureFaction.Scav, MapFeatureFaction.Scav, false)]
    [InlineData(MapOverlayKind.Spawns, MapFeatureFaction.Pmc, MapFeatureFaction.Pmc, true)]
    [InlineData(MapOverlayKind.Labels, MapFeatureFaction.Unknown, MapFeatureFaction.Scav, true)]
    public void The_raid_map_keeps_only_what_this_side_can_use(
        MapOverlayKind layer, MapFeatureFaction faction, MapFeatureFaction raid, bool kept) =>
        Assert.Equal(kept, RaidExtractSide.KeepOnMap(layer, faction, raid));

    [Fact]
    public void The_card_says_whose_exits_it_is_showing()
    {
        Assert.Equal("PMC raid · your extracts only", RaidCockpitViewModel.ExtractSideNoteFor(MapFeatureFaction.Pmc));
        Assert.Equal("Scav raid · your extracts only", RaidCockpitViewModel.ExtractSideNoteFor(MapFeatureFaction.Scav));
        Assert.Equal("Side unknown · PMC and Scav shown", RaidCockpitViewModel.ExtractSideNoteFor(MapFeatureFaction.Unknown));
    }

    private static MapSceneObject[] BothSides() =>
    [
        Object("a", MapSceneObjectKind.Extract, "ZB-1011", MapFeatureFaction.Pmc, MapSceneOfferState.Unknown),
        Object("b", MapSceneObjectKind.Extract, "Old Gas Station", MapFeatureFaction.Scav, MapSceneOfferState.Unknown),
        Object("c", MapSceneObjectKind.Extract, "RUAF Roadblock", MapFeatureFaction.Shared, MapSceneOfferState.Unknown),
        Object("d", MapSceneObjectKind.Transit, "Transit to Factory", MapFeatureFaction.Unknown, MapSceneOfferState.Unknown),
        Object("e", MapSceneObjectKind.Extract, "Unmarked Gap", MapFeatureFaction.Unknown, MapSceneOfferState.Unknown),
        Object("f", MapSceneObjectKind.SpawnArea, "Big Red", MapFeatureFaction.Pmc, MapSceneOfferState.Unknown),
    ];

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
