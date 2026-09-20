namespace TarkovCompanion.App.Services.V2.Shell;

/// <summary>
/// Whatever can say that a newer build is waiting.
/// </summary>
/// <remarks>
/// [#294] An interface rather than a direct reference to V1's settings page, for two reasons.
/// The shell should not have to know that the thing which tracks updates happens to be a V1 page
/// view model — that is an accident of where the updater was wired first. And the wiring is only
/// testable if something small can stand in for it: nothing in the unit suite constructs a whole
/// <see cref="ViewModels.V2.Shell.V2ShellViewModel"/>, because it needs the entire composition.
///
/// The gap this closes: V1 put a dot on the rail beside Settings when a build was waiting, and V2
/// had nothing at all outside Setup › Updates. The update channel is live and ships builds, so a
/// player who never opened that page was never told one had arrived.
/// </remarks>
public interface IUpdateWaitingSource
{
    /// <summary>Whether a build is waiting right now, fetched or not.</summary>
    /// <remarks>
    /// Read as well as subscribed to, because the event only fires on a change. A shell built
    /// after the updater had already found something would otherwise start with no mark and show
    /// one only if a second build arrived.
    /// </remarks>
    bool IsUpdateWaiting { get; }

    event EventHandler<bool>? UpdateWaitingChanged;
}
