using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels.V2.Shell;

namespace TarkovCompanion.UnitTests.V2Shell;

/// <summary>
/// [#294] A build that arrives says so, without the player going to look.
/// </summary>
/// <remarks>
/// V1 put a dot on its rail beside Settings when the updater had something; V2 put nothing
/// anywhere, so the only way to learn a build existed was to open Setup › Updates and read it.
/// The update channel is live and ships builds, so "nothing" meant a player stayed on an old one
/// indefinitely.
///
/// The wiring is tested through <see cref="V2ShellViewModel.MarkWhileUpdateWaits"/> rather than
/// through a built shell, because nothing in this suite can build one: it needs the whole
/// composition. The companion check that the shell still calls it is
/// <see cref="V2ShellHostContractTests"/>' source ratchet.
/// </remarks>
public sealed class V2UpdateNoticeTests
{
    [Fact]
    public void A_build_that_was_already_waiting_marks_setup_immediately()
    {
        // The event only fires on a change. A shell built after the updater found something must
        // still show the mark, or the notice depends on a second build arriving.
        var source = new FakeUpdates { IsUpdateWaiting = true };
        var setup = Setup();

        using var subscription = V2ShellViewModel.MarkWhileUpdateWaits(source, setup);

        Assert.True(setup.HasNotice);
    }

    [Fact]
    public void A_build_arriving_later_marks_setup()
    {
        var source = new FakeUpdates();
        var setup = Setup();
        using var subscription = V2ShellViewModel.MarkWhileUpdateWaits(source, setup);
        Assert.False(setup.HasNotice);

        source.Announce(true);

        Assert.True(setup.HasNotice);
    }

    [Fact]
    public void Installing_it_clears_the_mark()
    {
        var source = new FakeUpdates { IsUpdateWaiting = true };
        var setup = Setup();
        using var subscription = V2ShellViewModel.MarkWhileUpdateWaits(source, setup);

        source.Announce(false);

        Assert.False(setup.HasNotice);
    }

    /// <summary>A disposed shell stops being marked by an updater that outlives it.</summary>
    [Fact]
    public void A_disposed_subscription_stops_listening()
    {
        var source = new FakeUpdates();
        var setup = Setup();
        V2ShellViewModel.MarkWhileUpdateWaits(source, setup).Dispose();

        source.Announce(true);

        Assert.False(setup.HasNotice);
    }

    [Fact]
    public void The_mark_is_said_as_well_as_drawn()
    {
        var setup = Setup();
        Assert.Equal(string.Empty, setup.NoticeDescription);
        Assert.Equal(setup.SelectionDescription, setup.StatusDescription);

        setup.HasNotice = true;

        // A dot means nothing to a screen reader, and it is the only thing telling anybody.
        Assert.Equal("Update ready", setup.NoticeDescription);
        Assert.Contains("Update ready", setup.StatusDescription, StringComparison.Ordinal);
        // And it adds to what was announced rather than replacing it.
        Assert.Contains(setup.SelectionDescription, setup.StatusDescription, StringComparison.Ordinal);
    }

    [Fact]
    public void The_mark_notifies_so_the_rail_redraws()
    {
        var setup = Setup();
        var changed = new List<string?>();
        setup.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        setup.HasNotice = true;

        Assert.Contains(nameof(V2ShellDestinationViewModel.HasNotice), changed);
        Assert.Contains(nameof(V2ShellDestinationViewModel.StatusDescription), changed);
    }

    private static V2ShellDestinationViewModel Setup() =>
        new(V2ShellVariants.A.Setup, _ => { });

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
