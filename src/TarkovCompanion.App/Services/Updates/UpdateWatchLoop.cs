using Microsoft.Extensions.Logging;

namespace TarkovCompanion.App.Services.Updates;

/// <summary>
/// The automatic update check: once shortly after start, then every few hours, for the whole run.
/// </summary>
/// <remarks>
/// #888: the loop used to await the check bare. It is started fire-and-forget, so one throw (the
/// update-state file held by an antivirus scanner) ended it silently, and no automatic check ran
/// again until a restart; only the crash log's unobserved-task line ever said so. A failed check
/// is now logged and retried sooner, doubling back up to the ordinary interval.
/// </remarks>
public static class UpdateWatchLoop
{
    public static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(2);

    public static readonly TimeSpan Interval = TimeSpan.FromHours(4);

    /// <summary>The first retry after a failed check; each further failure doubles it.</summary>
    public static readonly TimeSpan FirstRetry = TimeSpan.FromMinutes(15);

    /// <summary>Runs until <paramref name="cancellationToken"/> is cancelled. Never throws otherwise.</summary>
    /// <param name="skip">True while a check would get in the way (an update in progress or waiting).</param>
    /// <param name="check">One check, including showing its result.</param>
    /// <param name="delay">Waits; <see cref="Task.Delay(TimeSpan, CancellationToken)"/> outside tests.</param>
    public static async Task RunAsync(
        Func<bool> skip,
        Func<CancellationToken, Task> check,
        CancellationToken cancellationToken,
        ILogger? logger = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(skip);
        ArgumentNullException.ThrowIfNull(check);
        delay ??= static (wait, token) => Task.Delay(wait, token);
        var next = FirstDelay;
        var retry = FirstRetry;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await delay(next, cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            next = Interval;
            if (skip())
            {
                continue;
            }

            try
            {
                await check(cancellationToken).ConfigureAwait(true);
                retry = FirstRetry;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                if (logger is not null)
                {
                    logger.LogWarning(exception, "Automatic update check failed; trying again in {Retry}", retry);
                }
                else
                {
                    Diagnostics.CrashLog.Write("updates", $"Automatic update check failed; trying again in {retry}. {exception}");
                }

                next = retry;
                retry = retry * 2 < Interval ? retry * 2 : Interval;
            }
        }
    }
}
