namespace TarkovCompanion.App.Services.Diagnostics;

/// <summary>
/// Holds a named load at its first line until released, so the page's loading state can be
/// photographed.
/// </summary>
/// <remarks>
/// [#279] A loading state lasts as long as the read behind it, which on a runner is a few hundred
/// milliseconds: a gallery that waits for it with a sleep photographs a finished page one run and
/// a half-drawn one the next. The <c>loading</c> gallery scene holds the loads named here instead,
/// answers "ready" once one of them is actually waiting, and releases them on the gallery's
/// "release" step, so the same launch also shows that the page leaves its loading state.
/// Nothing in the application sets this, and with nothing held it is one array read per load,
/// like <see cref="LoadFaultInjection"/> beside it.
/// </remarks>
internal static class LoadHold
{
    private static readonly object Gate = new();
    private static volatile string[] _held = [];
    private static TaskCompletionSource _release = NewRelease();
    private static readonly Dictionary<string, int> Waiting = new(StringComparer.Ordinal);

    public static void Hold(IEnumerable<string> surfaces)
    {
        lock (Gate)
        {
            _held = [.. surfaces.Select(surface => surface.Trim()).Where(surface => surface.Length > 0)];
            if (_release.Task.IsCompleted)
            {
                _release = NewRelease();
            }
        }
    }

    /// <summary>Lets every held load carry on, and holds nothing from now on.</summary>
    public static void ReleaseAll()
    {
        TaskCompletionSource release;
        lock (Gate)
        {
            _held = [];
            release = _release;
        }

        release.TrySetResult();
    }

    public static bool IsHeld(string surface) => Array.IndexOf(_held, surface) >= 0;

    /// <summary>Whether a load of <paramref name="surface"/> has reached its hold and is waiting there.</summary>
    public static bool IsWaiting(string surface)
    {
        lock (Gate)
        {
            return Waiting.TryGetValue(surface, out var count) && count > 0;
        }
    }

    /// <summary>Whether any load is waiting at a hold.</summary>
    public static bool AnyWaiting
    {
        get
        {
            lock (Gate)
            {
                return Waiting.Values.Any(count => count > 0);
            }
        }
    }

    /// <summary>Completes at once unless <paramref name="surface"/> is held; then when released or cancelled.</summary>
    public static Task WaitIfHeldAsync(string surface, CancellationToken cancellationToken) =>
        IsHeld(surface) ? WaitAsync(surface, cancellationToken) : Task.CompletedTask;

    private static async Task WaitAsync(string surface, CancellationToken cancellationToken)
    {
        Task release;
        lock (Gate)
        {
            release = _release.Task;
            Waiting[surface] = Waiting.GetValueOrDefault(surface) + 1;
        }

        try
        {
            await release.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (Gate)
            {
                Waiting[surface]--;
            }
        }
    }

    private static TaskCompletionSource NewRelease() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
