using Avalonia.Threading;

namespace TarkovCompanion.App.Services;

/// <summary>
/// Where deferred UI work is posted, and at what priority, for a view model that may or may not
/// be running inside a real Avalonia application.
/// </summary>
/// <remarks>
/// <para>
/// [V2 rough package 45] This started life as a <see cref="SynchronizationContext"/> subclass that
/// <see cref="Plan.PlanWorkspaceViewModel"/> handed to <see cref="DeferredDispatch"/>. It was never
/// installed — nothing in this application calls
/// <see cref="SynchronizationContext.SetSynchronizationContext"/> — but a type that derives from
/// <see cref="SynchronizationContext"/> and is built during shell construction reads like one that
/// is, and it cost a Windows verification run and a day of suspicion to establish that it was not.
/// A plain <c>Action&lt;Action&gt;</c> posts to the same dispatcher at the same priority and cannot
/// be mistaken for the ambient context, so that question cannot be asked again.
/// </para>
/// <para>
/// The priority is the point. Avalonia's own context posts at <see cref="DispatcherPriority.Default"/>,
/// which outranks <see cref="DispatcherPriority.Input"/>: deferring a filter through it takes the
/// work off the setter's own stack but still runs it once per character, ahead of the next key, so
/// the box stays as slow to type in as it was. At <see cref="DispatcherPriority.Background"/> the
/// keys already queued are handled first and a burst of them arrives as one pass at the end.
/// Background is not idle — it runs as soon as there is no input or rendering left to do, which for
/// a search box is the moment the typist pauses, not "eventually".
/// </para>
/// </remarks>
internal static class UiThreadPost
{
    /// <summary>
    /// Posts behind queued input when this is a real Avalonia application, and otherwise nothing.
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> means "run it inline", which is what a unit-test host and a headless
    /// run need and what every caller of this saw before the deferral existed.
    /// </remarks>
    public static Action<Action>? BehindInput() => BehindInput(SynchronizationContext.Current);

    /// <summary>The same decision with the ambient context handed in, so it can be tested.</summary>
    /// <remarks>
    /// The test is the namespace rather than the type because Avalonia's context is internal to it
    /// in some hosts and a headless run substitutes its own. It is the same test
    /// <c>MainWindowViewModel</c>, <c>V2ShellViewModel</c> and <c>RaidCockpitViewModel</c> already
    /// make; this is the one that is written down.
    /// </remarks>
    internal static Action<Action>? BehindInput(SynchronizationContext? current) =>
        IsAvalonia(current)
            ? static work => Dispatcher.UIThread.Post(work, DispatcherPriority.Background)
            : null;

    /// <summary>Whether this context is the one an Avalonia UI thread runs under.</summary>
    internal static bool IsAvalonia(SynchronizationContext? context) =>
        context?.GetType().Namespace?.StartsWith("Avalonia", StringComparison.Ordinal) == true;
}
