using TarkovCompanion.App.Services;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Where the quest search's deferred filter is posted, and that deciding it touches nothing.
/// </summary>
/// <remarks>
/// [V2 rough package 45] This decision was made by a <see cref="SynchronizationContext"/> subclass
/// built during shell construction, which read as though the application installed a context of its
/// own — it never did, but establishing that cost a Windows verification run and a day of suspicion.
/// It is a delegate now, and these are the cases the sniff has to get right: the one thing that can
/// silently turn the debounce off is deciding "not Avalonia" when it is, which makes typing slow
/// again with nothing to show for it.
/// </remarks>
public sealed class UiThreadPostTests
{
    [Fact]
    public void With_no_context_at_all_the_work_is_not_posted()
    {
        Assert.Null(UiThreadPost.BehindInput(null));
        Assert.False(UiThreadPost.IsAvalonia(null));
    }

    [Fact]
    public void A_test_hosts_own_context_is_not_a_dispatcher_to_post_to()
    {
        // The xunit synchronization context, or the plain one a background thread may carry:
        // posting a filter to either would run it somewhere that is not a UI thread.
        Assert.False(UiThreadPost.IsAvalonia(new SynchronizationContext()));
        Assert.Null(UiThreadPost.BehindInput(new SynchronizationContext()));
    }

    [Fact]
    public void Avalonias_context_is_recognised_by_the_namespace_it_lives_in()
    {
        var avalonia = new Avalonia.Threading.AvaloniaSynchronizationContext();

        Assert.True(UiThreadPost.IsAvalonia(avalonia));
        Assert.NotNull(UiThreadPost.BehindInput(avalonia));
    }

    [Fact]
    public void Deciding_where_to_post_does_not_change_the_ambient_context()
    {
        var before = SynchronizationContext.Current;

        UiThreadPost.BehindInput(new Avalonia.Threading.AvaloniaSynchronizationContext());

        Assert.Same(before, SynchronizationContext.Current);
    }

    [Fact]
    public void A_burst_posted_by_a_delegate_still_becomes_one_run()
    {
        var queued = new List<Action>();
        var runs = 0;
        var dispatch = DeferredDispatch.Posting(queued.Add, () => runs++);

        for (var index = 0; index < 12; index++)
        {
            dispatch.Request();
        }

        Assert.Equal(0, runs);
        Assert.Single(queued);
        queued[0]();
        Assert.Equal(1, runs);
    }

    [Fact]
    public void Without_somewhere_to_post_the_work_runs_at_once()
    {
        var runs = 0;
        var dispatch = DeferredDispatch.Posting(null, () => runs++);

        dispatch.Request();
        dispatch.Request();

        Assert.Equal(2, runs);
    }
}
