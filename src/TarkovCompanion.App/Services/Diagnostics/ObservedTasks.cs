namespace TarkovCompanion.App.Services.Diagnostics;

/// <summary>
/// Starts work nobody waits for without letting what it throws disappear.
/// </summary>
/// <remarks>
/// <c>_ = page.LoadAsync();</c> is the right shape for a load that must not hold up navigation and
/// the wrong way to write it: a task that faults outside the load's own catch has nowhere to
/// report, and its exception surfaces through <see cref="TaskScheduler.UnobservedTaskException"/>
/// whenever the collector gets round to it, if ever. "Panes that do not render until a restart,
/// and nothing in the log" (#453) is what that looks like from outside. This keeps the shape and
/// records the fault the moment it happens, in the file a player is asked to send.
/// </remarks>
internal static class ObservedTasks
{
    /// <param name="task">The work; null is tolerated so <c>page?.LoadAsync().Observe(…)</c> reads naturally.</param>
    /// <param name="surface">The workspace or page, in the shell's words: "plan", "hideout".</param>
    /// <param name="what">What was being attempted, as a phrase: "load", "refresh after a level change".</param>
    public static void Observe(this Task? task, string surface, string what)
    {
        if (task is null || task.IsCompletedSuccessfully)
        {
            return;
        }

        _ = task.ContinueWith(
            static (finished, state) =>
            {
                var (where, doing) = ((string, string))state!;
                if (finished.Exception?.GetBaseException() is { } failure and not OperationCanceledException)
                {
                    WorkspaceFault.Record(where, doing, failure);
                }
            },
            (surface, what),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
