using TarkovCompanion.App.Services.V2;
using TarkovCompanion.CompanionProtocol;

namespace TarkovCompanion.UnitTests.V2Raid;

/// <summary>#800: what the desktop does with a Control tablet's requests around a map switch.</summary>
public sealed class TabletRemoteWorkspaceApplierTests
{
    [Fact]
    public async Task MovesThatArriveDuringASwitchAndNameTheMapBeingLeftDoNotSendTheDeskBack()
    {
        var map = new FakeMap("customs");
        var applier = new TabletRemoteWorkspaceApplier(map);

        var switching = applier.SubmitAsync(Request("woods", 0, 0, 1));
        _ = applier.SubmitAsync(Request("customs", 10, 20, 2));
        _ = applier.SubmitAsync(Request("customs", 11, 21, 2));
        map.FinishSwitch();
        await switching;

        Assert.Equal("woods", map.CurrentMapId);
        Assert.Equal(["woods"], map.Switches);
        // The new map is left as it opened: the switch's own (0, 0) camera is not applied either.
        Assert.Empty(map.Cameras);
    }

    [Fact]
    public async Task OnlyTheNewestRequestWaitingBehindASwitchIsAppliedAndOnTheNewMap()
    {
        var map = new FakeMap("customs");
        var applier = new TabletRemoteWorkspaceApplier(map);

        var switching = applier.SubmitAsync(Request("woods", 0, 0, 1));
        _ = applier.SubmitAsync(Request("woods", 5, 5, 2));
        _ = applier.SubmitAsync(Request("Woods", 6, 7, 3));
        map.FinishSwitch();
        await switching;

        Assert.Equal(["woods"], map.Switches);
        Assert.Equal([(6.0, 7.0, 3.0)], map.Cameras);
    }

    [Fact]
    public async Task AMapTheDeskDoesNotCarryMovesNothing()
    {
        var map = new FakeMap("customs") { Known = ["customs"] };
        var applier = new TabletRemoteWorkspaceApplier(map);

        var switching = applier.SubmitAsync(Request("streets", 300, 300, 4));
        map.FinishSwitch();
        await switching;

        Assert.Equal("customs", map.CurrentMapId);
        Assert.Empty(map.Cameras);
    }

    private static WorkspaceProjection Request(string mapId, double x, double z, double zoom) => new(
        WorkspaceKind.Raid,
        mapId,
        "1F",
        new WorkspaceViewport(new MapCoordinate(mapId, "1F", CoordinateSpaceKind.World, "v1", x, null, z), zoom),
        null,
        [],
        [],
        null,
        [],
        [],
        [],
        null);

    private sealed class FakeMap(string mapId) : ITabletRemoteMap
    {
        private TaskCompletionSource _switch = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? CurrentMapId { get; private set; } = mapId;

        public string[]? Known { get; init; }

        public List<string> Switches { get; } = [];

        public List<(double X, double Z, double Zoom)> Cameras { get; } = [];

        public void FinishSwitch() => _switch.TrySetResult();

        public async Task SelectMapAsync(string mapId)
        {
            await _switch.Task;
            _switch = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (Known is { } known && !known.Contains(mapId, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            Switches.Add(mapId);
            CurrentMapId = mapId;
        }

        public void ApplyView(WorkspaceProjection projection, WorkspaceViewport? camera)
        {
            if (camera is not null)
            {
                Cameras.Add((camera.Center.X, camera.Center.Z, camera.Zoom));
            }
        }
    }
}
