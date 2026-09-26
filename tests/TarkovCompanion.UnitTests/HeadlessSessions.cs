using System.Reflection;
using Avalonia.Headless;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// <see cref="HeadlessUnitTestSession"/> with the one fault it has under load taken out.
/// </summary>
/// <remarks>
/// Avalonia 12.1.2's <c>StartNew</c> runs <c>task = Task.Run(...)</c> and, inside that very task,
/// builds the session from <c>task</c>. When the pool starts the task before <c>Task.Run</c> has
/// returned to the assignment — a busy machine, a parallel suite — the session keeps a null dispatch
/// task, and <c>Dispose</c> cancels its loop and then throws a NullReferenceException from
/// <c>_dispatchTask.Wait()</c>. That was every "harness Dispose" failure in MapMarkClipTests and
/// MapPanGestureTests: the test body had already passed. The loop has been told to stop by then, so
/// the only thing lost is the wait for it, and that is all this forgives: any other failure, or this
/// one on a session whose dispatch task is present, still fails the test.
///
/// Only one session may exist at a time. Each one sets up Avalonia's process-wide platform (the
/// render loop is bound to the dispatcher thread that set it up), and a second session starting
/// on another thread fails with "The calling thread cannot access this object because a
/// different thread owns it" from <c>DefaultRenderLoop.Add</c>. The collection attribute kept
/// most classes apart, but a class without it (StashScanWorkspaceViewModelTests, #866's first
/// Linux run) ran in parallel with them. So the session itself is the single owner now, whatever
/// collection the test is in.
///
/// That is not enough on its own, so every class that starts a session still belongs in
/// <c>AvaloniaHeadlessCollection</c> (no parallelization). A session's start resets Avalonia's
/// UI-thread dispatcher to none, and until the platform is set up the first thread in the process
/// to touch <c>Dispatcher.UIThread</c> becomes the UI thread. Any parallel test whose view model
/// posts to the dispatcher can be that thread. The Stash scan test still failed this way on
/// 2026-09-25 and 2026-09-26, with the semaphore in place, until its class joined the collection.
/// </remarks>
internal sealed class HeadlessSessions : IDisposable
{
    private static readonly FieldInfo? DispatchTask =
        typeof(HeadlessUnitTestSession).GetField("_dispatchTask", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly SemaphoreSlim Owner = new(1, 1);

    private static readonly TimeSpan OwnerWait = TimeSpan.FromMinutes(2);

    private readonly HeadlessUnitTestSession _session;
    private int _disposed;

    private HeadlessSessions(HeadlessUnitTestSession session) => _session = session;

    public static HeadlessSessions StartNew(Type entryPointType)
    {
        if (!Owner.Wait(OwnerWait))
        {
            throw new TimeoutException($"Another headless Avalonia session held the platform for over {OwnerWait.TotalMinutes} minutes.");
        }

        try
        {
            return new(HeadlessUnitTestSession.StartNew(entryPointType));
        }
        catch
        {
            Owner.Release();
            throw;
        }
    }

    public Task Dispatch(Action action, CancellationToken cancellationToken) =>
        _session.Dispatch(action, cancellationToken);

    public Task<TResult> Dispatch<TResult>(Func<TResult> action, CancellationToken cancellationToken) =>
        _session.Dispatch(action, cancellationToken);

    public Task<TResult> Dispatch<TResult>(Func<Task<TResult>> action, CancellationToken cancellationToken) =>
        _session.Dispatch(action, cancellationToken);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _session.Dispose();
        }
        catch (NullReferenceException) when (DispatchTask is not null && DispatchTask.GetValue(_session) is null)
        {
            // The race above: cancelled and completed, with nothing to wait on.
        }
        catch (AggregateException exception) when (IsLoopStoppedRace(exception))
        {
            // The same Dispose, the other order: CompleteAdding lands while the dispatch loop is
            // in BlockingCollection.Take, which throws rather than returning, and Wait rethrows it.
            // The loop has been told to stop and the test body has run; seen on RailGearClipTests
            // in a Linux gate run (2026-09-26) with every assertion passed.
        }
        finally
        {
            Owner.Release();
        }
    }

    private static bool IsLoopStoppedRace(AggregateException exception) =>
        exception.Flatten().InnerExceptions is { Count: > 0 } inner &&
        inner.All(error => error is InvalidOperationException &&
            error.StackTrace?.Contains("BlockingCollection", StringComparison.Ordinal) == true &&
            error.Message.Contains("marked as complete", StringComparison.Ordinal));
}
