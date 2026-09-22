using TarkovCompanion.App.Services;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// [#453] The Raid map's rebuild gate: a squad sharing positions asked for a rebuild three times a
/// second, each ahead of input, and Clayton's window stopped taking clicks for minutes.
/// </summary>
public sealed class PacedDispatchTests
{
    [Fact]
    public void Without_a_dispatcher_the_work_runs_at_once_every_time()
    {
        var runs = 0;
        var dispatch = PacedDispatch.ForInterfaceThread(null, () => runs++);

        dispatch.Request();
        dispatch.Request();

        Assert.Equal(2, runs);
    }

    [Fact]
    public void A_burst_of_requests_is_one_run()
    {
        var host = new Host();
        var runs = 0;
        var dispatch = host.Dispatch(() => runs++);

        dispatch.Request();
        dispatch.Request();
        dispatch.Request();
        host.RunPosted();

        Assert.Equal(1, runs);
    }

    [Fact]
    public void A_request_soon_after_a_run_waits_out_the_spacing_and_one_after_a_quiet_spell_does_not()
    {
        var host = new Host();
        var runs = 0;
        var dispatch = host.Dispatch(() => runs++);
        dispatch.Request();
        host.RunPosted();

        host.Now += TimeSpan.FromMilliseconds(100);
        dispatch.Request();
        dispatch.Request();
        host.RunPosted();

        Assert.Equal(1, runs);
        var (delay, run) = Assert.Single(host.Delayed);
        Assert.Equal(TimeSpan.FromMilliseconds(150), delay);
        host.Now += delay;
        run();
        Assert.Equal(2, runs);

        host.Delayed.Clear();
        host.Now += TimeSpan.FromSeconds(1);
        dispatch.Request();
        host.RunPosted();

        Assert.Equal(3, runs);
        Assert.Empty(host.Delayed);
    }

    [Fact]
    public void A_request_while_one_is_waiting_is_folded_into_it()
    {
        var host = new Host();
        var runs = 0;
        var dispatch = host.Dispatch(() => runs++);
        dispatch.Request();
        host.RunPosted();
        dispatch.Request();
        host.RunPosted();

        dispatch.Request();
        dispatch.Request();

        Assert.Empty(host.Posted);
        Assert.Single(host.Delayed);
    }

    private sealed class Host
    {
        public List<Action> Posted { get; } = [];

        public List<(TimeSpan Delay, Action Run)> Delayed { get; } = [];

        public TimeSpan Now { get; set; } = TimeSpan.FromSeconds(10);

        public PacedDispatch Dispatch(Action work) => new(
            work,
            Posted.Add,
            (delay, run) => Delayed.Add((delay, run)),
            () => Now,
            TimeSpan.FromMilliseconds(250));

        public void RunPosted()
        {
            var posted = Posted.ToArray();
            Posted.Clear();
            foreach (var run in posted)
            {
                run();
            }
        }
    }
}
