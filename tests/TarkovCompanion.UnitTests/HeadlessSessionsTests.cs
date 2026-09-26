using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using TarkovCompanion.UnitTests.V2MapRenderer;

namespace TarkovCompanion.UnitTests;

[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class HeadlessSessionsTests
{
    /// <summary>
    /// A thread that keeps asking for the UI-thread dispatcher, as a view model posting from a
    /// timer does, cannot become the UI thread while a session sets its platform up, nor post to
    /// the session's dispatcher before it is whole.
    /// </summary>
    [Fact]
    public async Task A_thread_touching_the_UI_dispatcher_cannot_take_it_from_the_session()
    {
        using var session = HeadlessSessions.StartNew(typeof(SessionApp));
        using var stop = new CancellationTokenSource();
        var halfBuilt = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var intruder = new Thread(() =>
        {
            // Touching is what claims the thread; posting on every pass would be work the session
            // could never drain, so one pass in ten thousand also posts.
            for (var pass = 0; !stop.IsCancellationRequested; pass++)
            {
                try
                {
                    var dispatcher = Dispatcher.UIThread;
                    if (pass % 10_000 == 0)
                    {
                        dispatcher.Post(static () => { });
                    }
                }
                catch (NullReferenceException error)
                {
                    // A post that reached a dispatcher before it had an implementation: on a
                    // timer thread this ends the test host.
                    halfBuilt.Enqueue(error);
                }
                catch (Exception)
                {
                    // Posting to a dispatcher that has shut down is not under test here.
                }
            }
        })
        {
            IsBackground = true,
            Name = "UI dispatcher intruder",
        };
        intruder.Start();

        try
        {
            for (var round = 0; round < 200; round++)
            {
                var owned = await session.Dispatch(() => Dispatcher.UIThread.CheckAccess(), CancellationToken.None);
                Assert.True(owned, $"Round {round}: the session is not the UI thread.");
            }
        }
        finally
        {
            stop.Cancel();
            intruder.Join();
        }

        Assert.Empty(halfBuilt);
    }

    public sealed class SessionApp : Avalonia.Application
    {
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<SessionApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }
}
