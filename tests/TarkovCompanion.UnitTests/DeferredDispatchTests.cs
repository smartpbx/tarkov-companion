using TarkovCompanion.App.Services;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// The rebuild gate for the Raid plan: one screenshot changes the marker, the trail, the group's
/// marks and the store's revision inside one turn of the dispatcher, all of them on the UI
/// thread, and used to rebuild the whole plan for each.
/// </summary>
public sealed class DeferredDispatchTests
{
    [Fact]
    public void Without_a_dispatcher_the_work_runs_at_once_every_time()
    {
        var runs = 0;
        var dispatch = new DeferredDispatch(null, () => runs++);

        dispatch.Request();
        dispatch.Request();

        Assert.Equal(2, runs);
    }

    [Fact]
    public void A_burst_of_requests_becomes_one_run_on_the_next_turn()
    {
        var context = new QueueContext();
        var runs = 0;
        var dispatch = new DeferredDispatch(context, () => runs++);

        for (var index = 0; index < 12; index++)
        {
            dispatch.Request();
        }

        Assert.Equal(0, runs);
        Assert.Equal(1, context.Pending);
        context.RunAll();
        Assert.Equal(1, runs);
    }

    [Fact]
    public void A_request_made_from_the_dispatcher_thread_is_still_deferred()
    {
        // CoalescingDispatch runs inline here, which is the behaviour this type exists not to have.
        var context = new QueueContext();
        var runs = 0;
        var dispatch = new DeferredDispatch(context, () => runs++);

        context.Send(_ => dispatch.Request(), null);

        Assert.Equal(0, runs);
        context.RunAll();
        Assert.Equal(1, runs);
    }

    [Fact]
    public void A_change_that_lands_while_the_work_runs_gets_one_more_pass()
    {
        var context = new QueueContext();
        var runs = 0;
        DeferredDispatch? dispatch = null;
        dispatch = new DeferredDispatch(context, () =>
        {
            runs++;
            if (runs == 1)
            {
                dispatch!.Request();
                dispatch.Request();
            }
        });

        dispatch.Request();
        context.RunAll();

        Assert.Equal(2, runs);
    }

    private sealed class QueueContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();

        public int Pending => _queue.Count;

        public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));

        public override void Send(SendOrPostCallback d, object? state) => d(state);

        public void RunAll()
        {
            while (_queue.Count > 0)
            {
                var (callback, state) = _queue.Dequeue();
                callback(state);
            }
        }
    }
}
