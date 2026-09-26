using System.Text.Json;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Workspaces;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;
using TarkovCompanion.GroupServer;

namespace TarkovCompanion.UnitTests.V2Raid;

/// <summary>[#919] The Draw bar's line width: remembered, bounded, and carried to the squad.</summary>
public sealed class DrawWidthTests
{
    [Fact]
    public void A_chosen_width_is_remembered_by_the_next_cockpit()
    {
        var layout = new MemoryLayout();
        var first = new DrawWidthSetting(layout);

        Assert.Equal(RaidDrawingWidths.Medium, first.Value);
        first.Set(RaidDrawingWidths.Thick);

        Assert.Equal("7", layout.Get(WorkspaceLayoutKeys.RaidDrawWidth));
        Assert.Equal(RaidDrawingWidths.Thick, new DrawWidthSetting(layout).Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("3")]
    [InlineData("99")]
    [InlineData("-2")]
    [InlineData("thick")]
    public void A_stored_width_that_is_not_one_of_the_three_is_medium(string? stored)
    {
        var layout = new MemoryLayout();
        if (stored is not null)
        {
            layout.Set(WorkspaceLayoutKeys.RaidDrawWidth, stored);
        }

        Assert.Equal(RaidDrawingWidths.Medium, new DrawWidthSetting(layout).Value);
    }

    [Fact]
    public void Setting_a_width_that_is_not_offered_stores_medium()
    {
        var layout = new MemoryLayout();
        Assert.Equal(RaidDrawingWidths.Medium, new DrawWidthSetting(layout).Set(40));
        Assert.Equal("4", layout.Get(WorkspaceLayoutKeys.RaidDrawWidth));
    }

    [Fact]
    public void The_store_keeps_a_line_at_the_width_it_was_drawn_and_bounds_it()
    {
        var store = new RaidDrawingStore();
        MapPoint[] points = [new(0, 0), new(10, 10)];

        var thin = store.Add("customs", null, points, RaidMarkScope.Private, RaidMarkLifetime.ThisRaid, RaidDrawingWidths.Thin);
        var wild = store.Add("customs", null, points, RaidMarkScope.Private, RaidMarkLifetime.ThisRaid, 500);
        var unstated = store.Add("customs", null, points, RaidMarkScope.Private, RaidMarkLifetime.ThisRaid);

        Assert.Equal(RaidDrawingWidths.Thin, thin!.Width);
        Assert.Equal(RaidDrawingWidths.Medium, wild!.Width);
        Assert.Equal(RaidDrawingWidths.Medium, unstated!.Width);
    }

    [Fact]
    public void The_width_crosses_the_wire_and_an_out_of_bounds_one_is_dropped()
    {
        var sent = new GroupDrawingView("abc", "customs", null, [(1, 2), (3, 4)], RaidDrawingWidths.Thick);
        var wire = GroupDrawingWire.Describe([sent])!;
        var json = JsonSerializer.Serialize(wire);

        Assert.Contains("\"width\":7", json, StringComparison.Ordinal);
        Assert.Equal(RaidDrawingWidths.Thick, Assert.Single(GroupDrawingWire.Read(wire)).Width);

        // From a squadmate on an older companion: no width, drawn as every line used to be.
        var old = JsonSerializer.Deserialize<List<GroupDrawingDto>>("""[{"id":"a","mapId":"customs","points":[1,2,3,4]}]""");
        Assert.Null(Assert.Single(GroupDrawingWire.Read(old)).Width);

        // Written by something other than a companion: the line is kept, the width is not.
        var odd = JsonSerializer.Deserialize<List<GroupDrawingDto>>("""[{"id":"a","mapId":"customs","points":[1,2,3,4],"width":900}]""");
        Assert.Null(Assert.Single(GroupDrawingWire.Read(odd)).Width);
    }

    [Fact]
    public void A_width_change_is_news_to_the_group()
    {
        var share = new GroupDrawingShare();
        var changes = 0;
        share.Changed += () => changes++;
        share.Set([new GroupDrawingView("a", "customs", null, [(1, 2), (3, 4)], 2)]);
        share.Set([new GroupDrawingView("a", "customs", null, [(1, 2), (3, 4)], 7)]);

        Assert.Equal(2, changes);
    }

    [Fact]
    public void The_relay_bounds_the_width_and_accepts_a_line_without_one()
    {
        GroupMemberState With(int? width) =>
            new GroupMemberState("Geo", "customs", "InRaid", null, null, null, null, null, [], [])
            {
                Drawings = [new GroupDrawingState("x", "customs", [1, 2, 3, 4]) { Width = width }],
            };

        Assert.Null(With(null).Validate());
        Assert.Null(With(1).Validate());
        Assert.Null(With(12).Validate());
        Assert.NotNull(With(0).Validate());
        Assert.NotNull(With(13).Validate());
    }

    [Fact]
    public void Own_lines_draw_at_their_width_and_an_old_squadmate_line_at_the_old_width()
    {
        var ours = new RaidDrawing(Guid.NewGuid(), "customs", null, [new(1, 1), new(4, 4)], DateTimeOffset.UnixEpoch, RaidMarkScope.Private, RaidMarkLifetime.ThisRaid, null, RaidDrawingWidths.Thick);
        var theirs = new GroupDrawingView("abc", "customs", null, [(10, 20), (30, 40)]);

        var (_, objects, styles) = RaidCockpitViewModel.BuildDrawingLayer(
            [ours],
            "customs",
            [("Riley", "#FF00FF00", theirs)],
            _ => true,
            position => new MapScenePoint(position.X, position.Z),
            DateTimeOffset.UnixEpoch);

        Assert.Equal(2, objects.Count);
        Assert.Equal((double)RaidDrawingWidths.Thick, (double)styles[objects[0].Id].LineThickness!);
        Assert.Equal((double)RaidDrawingWidths.Unstated, (double)styles[objects[1].Id].LineThickness!);
    }

    private sealed class MemoryLayout : IWorkspaceLayoutStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public string? Get(string key) => _values.GetValueOrDefault(key);

        public void Set(string key, string value) => _values[key] = value;
    }
}
