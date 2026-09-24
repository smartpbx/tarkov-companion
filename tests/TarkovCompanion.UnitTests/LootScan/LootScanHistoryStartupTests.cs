using TarkovCompanion.App.ViewModels.V2.LootScan;
using TarkovCompanion.Application.Services.LootScan;

namespace TarkovCompanion.UnitTests.LootScan;

/// <summary>
/// [#799] On a fresh launch the history listed saved scans about 0.8 s before migration 0019
/// created their table and logged "no such table".
/// </summary>
public sealed class LootScanHistoryStartupTests
{
    [Fact]
    public async Task The_first_listing_waits_until_the_database_is_ready()
    {
        var store = new CountingStore();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = new LootScanHistoryViewModel(store, databaseReady: _ => ready.Task);
        await Task.Delay(50);
        Assert.Equal(0, store.Listings);

        ready.SetResult();
        await store.Listed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, store.Listings);
    }

    [Fact]
    public async Task Without_a_readiness_signal_it_lists_at_once()
    {
        var store = new CountingStore();

        _ = new LootScanHistoryViewModel(store);

        await store.Listed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, store.Listings);
    }

    private sealed class CountingStore : ILootScanHistoryStore
    {
        private int _listings;

        public int Listings => Volatile.Read(ref _listings);

        public TaskCompletionSource Listed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SaveAsync(SavedLootScan scan, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<SavedLootScan>> ListForRaidAsync(Guid raidId, CancellationToken cancellationToken) =>
            List();

        public Task<IReadOnlyList<SavedLootScan>> ListRecentAsync(int limit, CancellationToken cancellationToken) =>
            List();

        private Task<IReadOnlyList<SavedLootScan>> List()
        {
            Interlocked.Increment(ref _listings);
            Listed.TrySetResult();
            return Task.FromResult<IReadOnlyList<SavedLootScan>>([]);
        }
    }
}
