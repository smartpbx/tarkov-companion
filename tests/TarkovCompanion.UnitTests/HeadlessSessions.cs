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
/// </remarks>
internal sealed class HeadlessSessions : IDisposable
{
    private static readonly FieldInfo? DispatchTask =
        typeof(HeadlessUnitTestSession).GetField("_dispatchTask", BindingFlags.Instance | BindingFlags.NonPublic);

    private readonly HeadlessUnitTestSession _session;

    private HeadlessSessions(HeadlessUnitTestSession session) => _session = session;

    public static HeadlessSessions StartNew(Type entryPointType) => new(HeadlessUnitTestSession.StartNew(entryPointType));

    public Task Dispatch(Action action, CancellationToken cancellationToken) =>
        _session.Dispatch(action, cancellationToken);

    public Task<TResult> Dispatch<TResult>(Func<TResult> action, CancellationToken cancellationToken) =>
        _session.Dispatch(action, cancellationToken);

    public Task<TResult> Dispatch<TResult>(Func<Task<TResult>> action, CancellationToken cancellationToken) =>
        _session.Dispatch(action, cancellationToken);

    public void Dispose()
    {
        try
        {
            _session.Dispose();
        }
        catch (NullReferenceException) when (DispatchTask is not null && DispatchTask.GetValue(_session) is null)
        {
            // The race above: cancelled and completed, with nothing to wait on.
        }
    }
}
