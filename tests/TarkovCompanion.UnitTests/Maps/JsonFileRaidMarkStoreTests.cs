using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Infrastructure.Maps;

namespace TarkovCompanion.UnitTests.Maps;

public sealed class JsonFileRaidMarkStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"raid-marks-{Guid.NewGuid():N}");

    private string StorePath => Path.Combine(_directory, "raid-marks.json");

    [Fact]
    public async Task AddedMarksSurviveALoadFromANewStoreInstance()
    {
        var first = new JsonFileRaidMarkStore(StorePath, new FakeTimeProvider(new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero)));
        var added = await first.AddAsync(RaidMarkKind.Waypoint, "factory", "ground", 25, 60, "Fall back here");

        var second = new JsonFileRaidMarkStore(StorePath);
        await second.LoadAsync();

        var reloaded = Assert.Single(second.Marks);
        Assert.Equal(added.Id, reloaded.Id);
        Assert.Equal(RaidMarkKind.Waypoint, reloaded.Kind);
        Assert.Equal("factory", reloaded.State.MapId);
        Assert.Equal("ground", reloaded.State.FloorId);
        Assert.Equal(25, reloaded.State.X);
        Assert.Equal(60, reloaded.State.Y);
        Assert.Equal("Fall back here", reloaded.State.Label);
    }

    [Fact]
    public async Task MoveUpdatesThePositionAndRemoveDropsTheMark()
    {
        var store = new JsonFileRaidMarkStore(StorePath);
        var mark = await store.AddAsync(RaidMarkKind.Ping, "factory", null, 10, 10, label: null);

        await store.MoveAsync(mark.Id, 15, 20);
        Assert.Equal(15, Assert.Single(store.Marks).State.X);
        Assert.Equal(20, Assert.Single(store.Marks).State.Y);

        await store.RemoveAsync(mark.Id);
        Assert.Empty(store.Marks);
    }

    [Fact]
    public async Task ChangedFiresForAddMoveAndRemove()
    {
        var store = new JsonFileRaidMarkStore(StorePath);
        // The lazy first load fires Changed on its own (empty is still a fact worth telling a
        // subscriber), so it is consumed here rather than folded into the count below.
        await store.LoadAsync();
        var raised = 0;
        store.Changed += () => raised++;

        var mark = await store.AddAsync(RaidMarkKind.Ping, "factory", null, 10, 10, label: null);
        await store.MoveAsync(mark.Id, 11, 11);
        await store.RemoveAsync(mark.Id);

        Assert.Equal(3, raised);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
