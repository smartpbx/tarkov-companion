using System.Collections.Concurrent;
using System.Reflection;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Avalonia's <see cref="HeadlessUnitTestSession"/> (12.1.2, per-test isolation), rebuilt so that
/// no other thread in the test process can take the UI thread from it.
/// </summary>
/// <remarks>
/// Every dispatched action rebuilds the application: <c>Dispatcher.ResetBeforeUnitTests()</c> sets
/// Avalonia's UI-thread dispatcher to none, then <c>AppBuilder.SetupUnsafe()</c> sets the headless
/// platform up again. In between, the first thread in the process to touch
/// <c>Dispatcher.UIThread</c> or <c>Dispatcher.CurrentDispatcher</c> creates a dispatcher, and the
/// first dispatcher created becomes the UI thread. The platform then builds its render loop, whose
/// <c>Add</c> checks the UI thread, and the session fails on its own thread with "The calling
/// thread cannot access this object because a different thread owns it". Any view model that
/// posts to the dispatcher from a pool thread can be that thread, and it need not be running a
/// test: work left behind by an earlier test does it too. xunit runs the headless collection after
/// every parallel test has finished, yet V2AppearanceApplierTests failed this way in #954's Linux
/// run (2026-09-26), so keeping classes apart cannot close this.
///
/// The same thread can also crash the test host: the setup swaps the new dispatcher's
/// implementation, and a post that lands mid-swap throws a NullReferenceException on a timer
/// thread (seen 2026-09-26 on this host, from a Now panel's one-second timer in a raid view model
/// that an earlier test never disposed; the run aborted).
///
/// Avalonia's session gives no way in between the calls, so this class does what it does, with
/// three changes. It holds <c>Dispatcher.s_globalLock</c>, which every dispatcher creation and
/// every UI-thread lookup that finds none takes, from the reset to the end of the setup, so a
/// thread that asks in that window waits. It builds the session's dispatcher while a stand-in
/// (a dispatcher whose thread has ended) is the UI thread, and only then publishes it: Avalonia's
/// constructor makes itself the UI thread before it has an implementation, and that half-built
/// dispatcher is what the timer above posted to. And it holds the new dispatcher's
/// <c>InstanceLock</c>, which every post takes, while the setup replaces the implementation. The
/// locks are re-entrant and only this thread takes them here. Everything else is a copy of Avalonia's <c>DispatchCore</c> and
/// <c>EnsureIsolatedApplication</c>; when Avalonia is upgraded, compare them. A renamed private
/// member fails every headless test at once with its name, rather than letting the race back in.
///
/// The loop runs on a dedicated thread that exists before the session is returned, which also
/// removes Avalonia's two Dispose races (a dispatch task still null under load, and
/// <c>BlockingCollection.Take</c> throwing once adding is complete) that this class used to forgive.
/// HeadlessSessionsTests fails on Avalonia's session and passes on this one.
///
/// Only one session may exist at a time, whatever collection the test is in: each one sets up
/// Avalonia's process-wide platform, and a second one on another thread fails from
/// <c>DefaultRenderLoop.Add</c>. Classes that start a session still belong in
/// <c>AvaloniaHeadlessCollection</c>, because the platform, the application and the locator are
/// process-wide while a session runs.
/// </remarks>
internal sealed class HeadlessSessions : IDisposable
{
    private const BindingFlags Internal = BindingFlags.NonPublic;

    private static readonly object DispatcherLock = Required(
        typeof(Dispatcher).GetField("s_globalLock", Internal | BindingFlags.Static),
        "Dispatcher.s_globalLock").GetValue(null)!;

    private static readonly FieldInfo UiThreadDispatcher = Required(
        typeof(Dispatcher).GetField("s_uiThread", Internal | BindingFlags.Static),
        "Dispatcher.s_uiThread");

    /// <summary>
    /// What another thread finds as the UI thread while the session builds its own dispatcher: a
    /// whole dispatcher whose thread has ended, so a post to it is queued and never run.
    /// </summary>
    private static readonly Dispatcher StandIn = CreateStandIn();

    private static readonly PropertyInfo InstanceLock = Required(
        typeof(Dispatcher).GetProperty("InstanceLock", Internal | BindingFlags.Instance),
        "Dispatcher.InstanceLock");

    private static readonly MethodInfo ResetBeforeUnitTests = Required(
        typeof(Dispatcher).GetMethod("ResetBeforeUnitTests", Internal | BindingFlags.Static, Type.EmptyTypes),
        "Dispatcher.ResetBeforeUnitTests()");

    private static readonly MethodInfo ResetForUnitTests = Required(
        typeof(Dispatcher).GetMethod("ResetForUnitTests", Internal | BindingFlags.Static, Type.EmptyTypes),
        "Dispatcher.ResetForUnitTests()");

    private static readonly MethodInfo Configure = Required(
        typeof(AppBuilder).GetMethod("Configure", Internal | BindingFlags.Static, [typeof(Type)]),
        "AppBuilder.Configure(Type)");

    private static readonly MethodInfo SetupUnsafe = Required(
        typeof(AppBuilder).GetMethod("SetupUnsafe", Internal | BindingFlags.Instance, Type.EmptyTypes),
        "AppBuilder.SetupUnsafe()");

    private static readonly Type ToolTipService = Required(
        typeof(Avalonia.Controls.Control).Assembly.GetType("Avalonia.Controls.IToolTipService"),
        "Avalonia.Controls.IToolTipService");

    // [PrivateApi]: public in the runtime assembly, left out of the reference assembly.
    private static readonly MethodInfo EnterLocatorScope = Required(
        typeof(AvaloniaLocator).GetMethod("EnterScope", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes),
        "AvaloniaLocator.EnterScope()");

    private static readonly PropertyInfo CurrentLocator = Required(
        typeof(AvaloniaLocator).GetProperty("Current", BindingFlags.Public | BindingFlags.Static),
        "AvaloniaLocator.Current");

    private static readonly MethodInfo GetLocatorService = Required(
        typeof(AvaloniaLocator).GetMethod("GetService", BindingFlags.Public | BindingFlags.Instance, [typeof(Type)]),
        "AvaloniaLocator.GetService(Type)");

    private static readonly SemaphoreSlim Owner = new(1, 1);

    private static readonly TimeSpan OwnerWait = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan StopWait = TimeSpan.FromMinutes(1);

    private readonly BlockingCollection<(Action Work, ExecutionContext? Context)> _queue = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly Thread _thread;
    private AppBuilder? _appBuilder;
    private int _disposed;

    private HeadlessSessions(Type entryPointType)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() => Run(entryPointType, started))
        {
            IsBackground = true,
            Name = "Headless Avalonia session",
        };
        _thread.Start();
        started.Task.GetAwaiter().GetResult();
    }

    public static HeadlessSessions StartNew(Type entryPointType)
    {
        if (!Owner.Wait(OwnerWait))
        {
            throw new TimeoutException($"Another headless Avalonia session held the platform for over {OwnerWait.TotalMinutes} minutes.");
        }

        try
        {
            return new(entryPointType);
        }
        catch
        {
            Owner.Release();
            throw;
        }
    }

    public Task Dispatch(Action action, CancellationToken cancellationToken) =>
        DispatchCore(
            () =>
            {
                action();
                return Task.FromResult(0);
            },
            cancellationToken);

    public Task<TResult> Dispatch<TResult>(Func<TResult> action, CancellationToken cancellationToken) =>
        DispatchCore(() => Task.FromResult(action()), cancellationToken);

    public Task<TResult> Dispatch<TResult>(Func<Task<TResult>> action, CancellationToken cancellationToken) =>
        DispatchCore(action, cancellationToken);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _stopping.Cancel();
            _queue.CompleteAdding();
            if (!_thread.Join(StopWait))
            {
                throw new TimeoutException($"The headless Avalonia session did not stop within {StopWait.TotalMinutes} minute.");
            }

            _stopping.Dispose();
        }
        finally
        {
            Owner.Release();
        }
    }

    private void Run(Type entryPointType, TaskCompletionSource started)
    {
        try
        {
            var appBuilder = (AppBuilder)Invoke(Configure, null, entryPointType)!;
            if (appBuilder.WindowingSubsystemName != "Headless")
            {
                appBuilder = appBuilder.UseHeadless(new AvaloniaHeadlessPlatformOptions());
            }

            if (appBuilder.TextShapingSubsystemInitializer is null)
            {
                appBuilder = appBuilder.UseHarfBuzz();
            }

            _appBuilder = appBuilder;
        }
        catch (Exception error)
        {
            started.SetException(error);
            return;
        }

        started.SetResult();
        try
        {
            while (_queue.TryTake(out var item, Timeout.Infinite, _stopping.Token))
            {
                if (item.Context is not null)
                {
                    ExecutionContext.Run(item.Context, static work => ((Action)work!).Invoke(), item.Work);
                }
                else
                {
                    item.Work();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed: the queue is abandoned, as Avalonia's session abandons it.
        }
    }

    private Task<TResult> DispatchCore<TResult>(Func<Task<TResult>> action, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_stopping.IsCancellationRequested, this);

        var stopping = _stopping.Token;
        var executionContext = ExecutionContext.Capture();
        // Asynchronous continuations, unlike Avalonia's: an inline one ran the rest of the test,
        // and so Dispose, on this session's own thread, which then waited for itself.
        var completion = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add((() =>
        {
            var cts = new CancellationTokenSource();
            using var globalCts = stopping.Register(static s => ((CancellationTokenSource)s!).Cancel(), cts, true);
            using var localCts = cancellationToken.Register(static s => ((CancellationTokenSource)s!).Cancel(), cts, true);

            IDisposable application;
            try
            {
                application = EnterIsolatedApplication();
            }
            catch (Exception error)
            {
                completion.TrySetException(error);
                return;
            }

            var shouldCancel = false;
            Exception? caught = null;
            TResult result = default!;
            try
            {
                var task = action();
                if (task.Status != TaskStatus.RanToCompletion)
                {
                    task.ContinueWith(
                        static (_, s) => ((CancellationTokenSource)s!).Cancel(),
                        cts,
                        TaskScheduler.FromCurrentSynchronizationContext());

                    if (cts.IsCancellationRequested)
                    {
                        shouldCancel = true;
                    }
                    else
                    {
                        var frame = new DispatcherFrame();
                        using var innerCts = cts.Token.Register(() => frame.Continue = false, true);
                        Dispatcher.UIThread.PushFrame(frame);
                        result = task.GetAwaiter().GetResult();
                    }
                }
                else
                {
                    result = task.GetAwaiter().GetResult();
                }
            }
            catch (Exception error)
            {
                caught = error;
            }
            finally
            {
                try
                {
                    application.Dispose();
                }
                catch (Exception error)
                {
                    caught = error;
                }
            }

            if (caught is not null)
            {
                completion.TrySetException(caught);
            }
            else if (shouldCancel)
            {
                completion.TrySetCanceled(cts.Token);
            }
            else
            {
                completion.TrySetResult(result);
            }
        }, executionContext));
        return completion.Task;
    }

    private IDisposable EnterIsolatedApplication()
    {
        var scope = (IDisposable)Invoke(EnterLocatorScope, null)!;
        var oldContext = SynchronizationContext.Current;
        try
        {
            // The one departure from Avalonia: no other thread can create the UI-thread
            // dispatcher between the reset and the setup, or reach it before it is whole, or post
            // to it while the setup swaps its implementation (see the remarks).
            lock (DispatcherLock)
            {
                Invoke(ResetBeforeUnitTests, null);
                UiThreadDispatcher.SetValue(null, StandIn);
                var dispatcher = Dispatcher.CurrentDispatcher;
                UiThreadDispatcher.SetValue(null, dispatcher);
                lock (InstanceLock.GetValue(dispatcher)!)
                {
                    Invoke(SetupUnsafe, _appBuilder);
                }
            }
        }
        catch
        {
            scope.Dispose();
            throw;
        }

        return new Cleanup(() =>
        {
            try
            {
                (LocatorService(ToolTipService) as IDisposable)?.Dispose();
                (LocatorService(typeof(FontManager)) as IDisposable)?.Dispose();
                Invoke(ResetForUnitTests, null);
            }
            finally
            {
                try
                {
                    scope.Dispose();
                }
                finally
                {
                    Invoke(ResetBeforeUnitTests, null);
                    SynchronizationContext.SetSynchronizationContext(oldContext);
                }
            }
        });
    }

    private static Dispatcher CreateStandIn()
    {
        Dispatcher? standIn = null;
        var thread = new Thread(() => standIn = Dispatcher.CurrentDispatcher) { IsBackground = true };
        thread.Start();
        thread.Join();
        return standIn!;
    }

    private static object? LocatorService(Type service) =>
        Invoke(GetLocatorService, CurrentLocator.GetValue(null), service);

    /// <summary>Reflection without the wrapper, so a failure reads as Avalonia's own exception.</summary>
    private static object? Invoke(MethodInfo method, object? target, params object?[] arguments)
    {
        try
        {
            return method.Invoke(target, arguments);
        }
        catch (TargetInvocationException wrapped) when (wrapped.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(wrapped.InnerException).Throw();
            throw;
        }
    }

    private static T Required<T>(T? member, string name)
        where T : class =>
        member ?? throw new InvalidOperationException(
            $"Avalonia no longer has {name}. HeadlessSessions copies HeadlessUnitTestSession's per-test setup; compare it with the new Avalonia version.");

    private sealed class Cleanup(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
