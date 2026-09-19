using Avalonia.Threading;

namespace TarkovCompanion.App.Services;

/// <summary>
/// Posts to the UI thread behind whatever input is already queued, for work that must not run
/// between two keystrokes.
/// </summary>
/// <remarks>
/// <see cref="DeferredDispatch"/> coalesces through whatever <see cref="SynchronizationContext"/>
/// it is handed, and the one Avalonia installs posts at <see cref="DispatcherPriority.Default"/>,
/// which outranks <see cref="DispatcherPriority.Input"/>. Deferring a filter through that context
/// takes it off the setter's own stack but still runs it once per character, ahead of the next key:
/// the box stays as slow to type in as it was. At <see cref="DispatcherPriority.Background"/> the
/// keys already waiting are handled first, so a burst of them arrives as one filter pass at the end.
///
/// Background is not idle. It runs as soon as there is no input or rendering left to do, which for
/// a search box is the moment the typist pauses, not "eventually".
/// </remarks>
public sealed class BehindInputSynchronizationContext : SynchronizationContext
{
    public override void Post(SendOrPostCallback d, object? state) =>
        Dispatcher.UIThread.Post(() => d(state), DispatcherPriority.Background);

    public override void Send(SendOrPostCallback d, object? state) =>
        Dispatcher.UIThread.Invoke(() => d(state));
}
