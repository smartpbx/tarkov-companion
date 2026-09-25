using TarkovCompanion.App.ViewModels.V2.Shell;
using TarkovCompanion.App.ViewModels.V2.StashScan;
using TarkovCompanion.Application.Services.StashScan;

namespace TarkovCompanion.UnitTests.StashScan;

// #871/#872: Stash is loading, empty, loaded or failed, and says only that state's message.
public sealed partial class StashScanWorkspaceViewModelTests
{
    [Fact]
    public async Task While_snapshots_are_read_it_says_loading_and_not_empty_then_empty()
    {
        var store = new FakeSnapshotStore { ListGate = new TaskCompletionSource() };
        var viewModel = StateViewModel(store);

        var load = viewModel.LoadAsync();

        Assert.Equal(PageLoadState.Loading, viewModel.LoadState);
        Assert.Equal("Loading stash snapshots…", viewModel.Status);
        Assert.False(viewModel.ShowsNoSnapshots);
        Assert.False(viewModel.ShowsNothingScanned);

        store.ListGate.SetResult();
        await load;

        Assert.Equal(PageLoadState.Empty, viewModel.LoadState);
        Assert.True(viewModel.ShowsNoSnapshots);
        Assert.True(viewModel.ShowsNothingScanned);
        Assert.False(viewModel.LoadFault.IsVisible);
    }

    [Fact]
    public async Task A_failed_read_says_so_in_words_not_empty_and_retry_brings_the_snapshots_back()
    {
        var store = new FakeSnapshotStore
        {
            ListFailure = new IOException("The process cannot access the file 'tarkov-companion.db'."),
        };
        store.Seed(Record());
        var viewModel = StateViewModel(store);

        await viewModel.LoadAsync();

        Assert.Equal(PageLoadState.Failed, viewModel.LoadState);
        Assert.True(viewModel.LoadFault.IsVisible);
        Assert.Equal("Couldn't read your stash snapshots", viewModel.LoadFault.Title);
        Assert.DoesNotContain("process", viewModel.LoadFault.Title + viewModel.LoadFault.Detail + viewModel.Status, StringComparison.Ordinal);
        Assert.False(viewModel.ShowsNoSnapshots);
        Assert.False(viewModel.ShowsNothingScanned);

        store.ListFailure = null;
        await viewModel.LoadFault.RetryAsync();

        Assert.Equal(PageLoadState.Loaded, viewModel.LoadState);
        Assert.False(viewModel.LoadFault.IsVisible);
        Assert.True(viewModel.HasSnapshots);
        Assert.False(viewModel.ShowsNoSnapshots);
    }

    private static StashScanWorkspaceViewModel StateViewModel(FakeSnapshotStore store)
    {
        var reviewCommands = new InMemoryStashReviewCommandSink();
        return new StashScanWorkspaceViewModel(
            store,
            Workflow(store, reviewCommands),
            reviewCommands,
            new FakeItemFactCatalog([], []),
            new FakeRuntimeStateStore(RuntimeSnapshot()));
    }
}
