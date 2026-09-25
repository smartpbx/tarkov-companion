namespace TarkovCompanion.App.ViewModels.V2.Shell;

/// <summary>
/// Attaches <see cref="V2UpdateReadyNoticeViewModel"/> to the shell (#881).
/// </summary>
/// <remarks>
/// A separate partial, the shape <c>V2ShellViewModel.ControlRequest.cs</c> uses: the Setup
/// workspace, and with it the updater's settings page, already exists (or is null under the
/// render/test constructor) by the time anything reads this, so nothing is wired in the main
/// constructor. The same source already drives the gear's dot through
/// <see cref="MarkWhileUpdateWaits"/>; this adds the words and the button.
/// </remarks>
public sealed partial class V2ShellViewModel
{
    private V2UpdateReadyNoticeViewModel? _updateReadyNotice;

    /// <summary>"Update ready · Restart", above the gear while a build waits.</summary>
    public V2UpdateReadyNoticeViewModel UpdateReadyNotice =>
        _updateReadyNotice ??= SetupWorkspace?.Settings is { } settings
            ? new V2UpdateReadyNoticeViewModel(settings, settings.UpdateNowCommand, () => settings.AvailableBuild)
            : new V2UpdateReadyNoticeViewModel(null, null);
}
