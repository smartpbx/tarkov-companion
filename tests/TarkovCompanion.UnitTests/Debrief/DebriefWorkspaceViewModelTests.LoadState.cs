using TarkovCompanion.App.ViewModels.V2.Debrief;
using TarkovCompanion.App.ViewModels.V2.Shell;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.UnitTests.Debrief;

// #871/#872: Debrief is loading, empty, loaded or failed, and says only that state's message.
public sealed partial class DebriefWorkspaceViewModelTests
{
    [Fact]
    public async Task While_the_history_is_read_it_says_loading_and_not_empty_then_empty()
    {
        var service = new FakeRaidHistoryService { ListGate = new TaskCompletionSource() };
        var viewModel = new DebriefWorkspaceViewModel(service, TestPaths());

        var load = viewModel.LoadAsync();

        Assert.Equal(PageLoadState.Loading, viewModel.LoadState);
        Assert.Equal("Loading raid history…", viewModel.Status);
        Assert.False(viewModel.ShowsNoRaids);
        Assert.False(viewModel.LoadFault.IsVisible);

        service.ListGate.SetResult();
        await load;

        Assert.Equal(PageLoadState.Empty, viewModel.LoadState);
        Assert.True(viewModel.ShowsNoRaids);
        Assert.Equal("No raids recorded yet.", viewModel.NoRaidsMessage);
        Assert.False(viewModel.LoadFault.IsVisible);
    }

    [Fact]
    public async Task A_failed_read_says_so_in_words_not_empty_and_retry_brings_the_raids_back()
    {
        var service = new FakeRaidHistoryService
        {
            ListFailure = new InvalidOperationException("SQLite Error 11: 'database disk image is malformed'."),
        };
        service.Seed(new RaidHistoryEntry(RaidId, Guid.NewGuid(), "customs", "Pmc", Started, Started.AddMinutes(24), "Survived", null));
        var viewModel = new DebriefWorkspaceViewModel(service, TestPaths());

        await viewModel.LoadAsync();

        Assert.Equal(PageLoadState.Failed, viewModel.LoadState);
        Assert.True(viewModel.LoadFault.IsVisible);
        Assert.Equal("Couldn't read your raid history", viewModel.LoadFault.Title);
        Assert.DoesNotContain("SQLite", viewModel.LoadFault.Title + viewModel.LoadFault.Detail + viewModel.Status, StringComparison.Ordinal);
        Assert.False(viewModel.ShowsNoRaids);

        service.ListFailure = null;
        await viewModel.LoadFault.RetryAsync();

        Assert.Equal(PageLoadState.Loaded, viewModel.LoadState);
        Assert.False(viewModel.LoadFault.IsVisible);
        Assert.Single(viewModel.Raids);
        Assert.Equal("1 raid", viewModel.Status);
    }
}
