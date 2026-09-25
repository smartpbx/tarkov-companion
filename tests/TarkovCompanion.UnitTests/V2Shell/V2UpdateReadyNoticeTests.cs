using System.Windows.Input;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels.V2.Shell;

namespace TarkovCompanion.UnitTests.V2Shell;

/// <summary>
/// [#881] "Update ready · Restart": a waiting build said in words above the gear, dismissible for
/// that build only.
/// </summary>
public sealed class V2UpdateReadyNoticeTests
{
    [Fact]
    public void A_build_already_waiting_shows_the_notice_at_once()
    {
        using var notice = new V2UpdateReadyNoticeViewModel(new FakeUpdates { IsUpdateWaiting = true }, new Install());

        Assert.True(notice.IsVisible);
        Assert.Equal("Update ready", notice.Label);
        Assert.Equal("Restart", notice.ActionLabel);
    }

    [Fact]
    public void A_build_arriving_later_shows_it_and_says_so()
    {
        var source = new FakeUpdates();
        using var notice = new V2UpdateReadyNoticeViewModel(source, new Install());
        var changed = new List<string?>();
        notice.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.False(notice.IsVisible);

        source.Announce(true);

        Assert.True(notice.IsVisible);
        Assert.Contains(nameof(V2UpdateReadyNoticeViewModel.IsVisible), changed);
    }

    [Fact]
    public void Dismissing_hides_it_for_this_build_and_a_later_build_shows_it_again()
    {
        var source = new FakeUpdates { IsUpdateWaiting = true };
        using var notice = new V2UpdateReadyNoticeViewModel(source, new Install());

        notice.DismissCommand.Execute(null);
        Assert.False(notice.IsVisible);

        // The same build announced again (a re-check) is not new news.
        source.Announce(true);
        Assert.False(notice.IsVisible);

        // Installed or withdrawn, then another build: that one is.
        source.Announce(false);
        Assert.False(notice.IsVisible);
        source.Announce(true);
        Assert.True(notice.IsVisible);
    }

    [Fact]
    public void Restart_runs_the_one_press_update()
    {
        var install = new Install();
        using var notice = new V2UpdateReadyNoticeViewModel(new FakeUpdates { IsUpdateWaiting = true }, install);

        notice.InstallCommand!.Execute(null);

        Assert.Equal(1, install.Runs);
    }

    [Fact]
    public void The_version_is_named_when_the_feed_gave_one_and_not_when_it_gave_a_sentence()
    {
        var version = "2.0.1700";
        using var notice = new V2UpdateReadyNoticeViewModel(new FakeUpdates { IsUpdateWaiting = true }, new Install(), () => version);

        Assert.Contains("2.0.1700", notice.Description, StringComparison.Ordinal);

        version = "Nothing newer";
        Assert.Equal("Update ready", notice.Description);
    }

    [Fact]
    public void Without_an_updater_there_is_nothing_to_show()
    {
        using var notice = new V2UpdateReadyNoticeViewModel(null, null);

        Assert.False(notice.IsVisible);
    }

    [Fact]
    public void A_disposed_notice_stops_listening()
    {
        var source = new FakeUpdates();
        var notice = new V2UpdateReadyNoticeViewModel(source, new Install());
        notice.Dispose();

        source.Announce(true);

        Assert.False(notice.IsVisible);
    }

    private sealed class Install : ICommand
    {
        public int Runs { get; private set; }

        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => Runs++;
    }

    private sealed class FakeUpdates : IUpdateWaitingSource
    {
        public bool IsUpdateWaiting { get; set; }

        public event EventHandler<bool>? UpdateWaitingChanged;

        public void Announce(bool waiting)
        {
            IsUpdateWaiting = waiting;
            UpdateWaitingChanged?.Invoke(this, waiting);
        }
    }
}
