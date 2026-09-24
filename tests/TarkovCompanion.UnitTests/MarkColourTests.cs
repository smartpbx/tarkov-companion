using System.Text.Json;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps.Scene;
using TarkovCompanion.GroupServer;
using TarkovCompanion.Infrastructure.Maps;

namespace TarkovCompanion.UnitTests;

/// <summary>#290: a mark's chosen colour, from the palette, through the desktop store, the relay and the maps.</summary>
public sealed class MarkColourTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 20, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("#56b4e9", "#56B4E9")]
    [InlineData(" #CC79A7 ", "#CC79A7")]
    [InlineData("#22D3EE", null)] // the waypoint colour every page before #290 sent
    [InlineData("#C7A66B", null)] // and its ping colour
    [InlineData("red", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void OnlyAPaletteColourIsAColour(string? value, string? expected)
    {
        Assert.Equal(expected, MarkPalette.Normalize(value));
    }

    [Fact]
    public void ThePaletteIsSixNamedColours()
    {
        Assert.Equal(6, MarkPalette.Colours.Count);
        Assert.Equal(6, MarkPalette.Colours.Select(colour => colour.Hex).Distinct().Count());
        Assert.All(MarkPalette.Colours, colour => Assert.False(string.IsNullOrWhiteSpace(colour.Name)));
        Assert.Equal("Orange", MarkPalette.NameOf("#e69f00"));
    }

    [Fact]
    public async Task TheDesktopStoreKeepsAChosenColourAcrossARestart()
    {
        var directory = Directory.CreateTempSubdirectory("mark-colour-");
        try
        {
            var path = Path.Combine(directory.FullName, "marks.json");
            using (var store = new JsonFileRaidMarkStore(path))
            {
                await store.LoadAsync();
                await store.PlaceAsync("customs", null, 1, 2, null, RaidMarkScope.Squad, RaidMarkLifetime.UntilRemoved, colour: "#009e73");
                await store.PlaceAsync("customs", null, 3, 4, null, RaidMarkScope.Squad, RaidMarkLifetime.UntilRemoved, colour: "#123456");
            }

            using var reopened = new JsonFileRaidMarkStore(path);
            await reopened.LoadAsync();
            Assert.Equal(["#009E73", null], reopened.Marks.OrderBy(mark => mark.State.X).Select(mark => mark.Colour));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void TheRelayKeepsAPaletteColourAndDropsAnythingElse()
    {
        var marks = new GroupMarks(new MovableClock(Now));

        var coloured = marks.AddWaypoint("room", "Geo", "customs", 1, 2, 3, null, "#e69f00");
        var painted = marks.AddPing("room", "Geo", "customs", 1, 2, 3, null, "#FF00FF");
        var plain = marks.AddWaypoint("room", "Geo", "customs", 1, 2, 3, null);

        Assert.Equal("#E69F00", coloured.Color);
        Assert.Null(painted.Color);
        // A mark with no colour is written exactly as before, so older desktops see the same shape.
        Assert.DoesNotContain("color", JsonSerializer.Serialize(plain), StringComparison.Ordinal);
        Assert.Contains("\"color\":\"#E69F00\"", JsonSerializer.Serialize(coloured), StringComparison.Ordinal);
    }

    [Fact]
    public void TheRelaysMarkRequestReadsAColourAndIgnoresABadOne()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var good = JsonSerializer.Deserialize<MarkRequest>("""{"by":"Geo","mapId":"customs","x":1,"y":2,"z":3,"color":"#d55e00"}""", web)!;
        var bad = JsonSerializer.Deserialize<MarkRequest>("""{"by":"Geo","mapId":"customs","x":1,"y":2,"z":3,"color":"url(x)"}""", web)!;
        var none = JsonSerializer.Deserialize<MarkRequest>("""{"by":"Geo","mapId":"customs","x":1,"y":2,"z":3}""", web)!;

        Assert.Equal("#D55E00", good.PaletteColor);
        Assert.Null(bad.PaletteColor);
        Assert.Null(bad.Validate());
        Assert.Null(none.PaletteColor);
    }

    [Fact]
    public void RaidMapColoursTheirOwnAndTheSquadsMarksByTheIdsTheyAreDrawnUnder()
    {
        var own = new RaidMark(Guid.NewGuid(), RaidMarkKind.Waypoint, new MapMarkState("customs", null, 1, 2, null, null), Now) { Colour = "#F0E442" };
        var plain = new RaidMark(Guid.NewGuid(), RaidMarkKind.Ping, new MapMarkState("customs", null, 1, 2, null, Now.AddSeconds(45)), Now);
        var waypoint = new GroupWaypointView(7, "Geo", "customs", 1, 2, 3, null, null) { Colour = "#56B4E9" };
        var ping = new GroupPingView(8, "Geo", "customs", 1, 2, 3, null, Now) { Colour = "#cc79a7" };

        var colours = RaidCockpitViewModel.MarkColoursFor([own, plain], [waypoint], [ping]);

        Assert.Equal(3, colours.Count);
        Assert.Equal("#F0E442", colours[$"mark:{own.Id}"]);
        Assert.Equal("#56B4E9", colours["group-waypoint:7"]);
        Assert.Equal("#CC79A7", colours["group-ping:8"]);

        // Laid over an existing style (a route's dash, say) rather than replacing it.
        var styles = RaidCockpitViewModel.WithMarkColours(
            new Dictionary<MapSceneObjectId, MapSceneObjectStyle> { [new($"mark:{own.Id}")] = new(Dashed: true) },
            colours);
        Assert.Equal(new MapSceneObjectStyle(Color: "#F0E442", Dashed: true), styles[new($"mark:{own.Id}")]);
        Assert.Equal("#56B4E9", styles[new("group-waypoint:7")].Color);
    }

    private sealed class MovableClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
