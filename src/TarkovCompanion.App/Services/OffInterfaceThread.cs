namespace TarkovCompanion.App.Services;

/// <summary>
/// Runs a view model's reading and projecting on the thread pool, so only the result comes back.
/// </summary>
/// <remarks>
/// #453. A load that looks up one item per row — a name for each of 223 keep-list rows, a price for
/// each — awaited every lookup from the interface thread. While SQLite ran on the caller's thread
/// that was several hundred queries inside one dispatcher turn, which is a frozen window. Once
/// <c>SqliteConnectionFactory</c> moved each query to the pool it became several hundred round
/// trips through the dispatcher instead: no longer a freeze, but a pane that fills slowly and a
/// dispatcher woken a thousand times to do nothing but pass a result along.
///
/// Inside <see cref="Run{T}"/> there is no synchronization context, so every await in the loop,
/// whatever it was configured with, continues on the pool, and the repository — already on a pool
/// thread — does not hop again. The work passed in must not touch anything a view is bound to: it
/// reads, builds plain objects, and returns them for the caller to assign.
/// </remarks>
internal static class OffInterfaceThread
{
    public static Task<T> Run<T>(Func<Task<T>> work, CancellationToken cancellationToken = default) =>
        Task.Run(work, cancellationToken);

    public static Task Run(Func<Task> work, CancellationToken cancellationToken = default) =>
        Task.Run(work, cancellationToken);
}
