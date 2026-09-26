using TarkovCompanion.App.Localization;

namespace TarkovCompanion.App.ViewModels.V2.Shell;

/// <summary>One row of the unrecognised tray: when, the best guess, and "Read as…".</summary>
/// <param name="Detail">The guesses and why nobody was sure, for the tooltip.</param>
public sealed record V2UnrecognisedScreenViewModel(string ArtifactId, string Label, string Detail, ScanSourceViewModel Source)
{
    public string AutomationId => $"v2-shell-unrecognised-{ArtifactId}";
}

/// <summary>[#712 1-1] The unrecognised tray in the capture panel, where the intent picker was.</summary>
public sealed partial class V2ShellViewModel
{
    private IReadOnlyList<V2UnrecognisedScreenViewModel> _unrecognisedScreens = [];

    public IReadOnlyList<V2UnrecognisedScreenViewModel> UnrecognisedScreens => _unrecognisedScreens;

    public bool HasUnrecognisedScreens => _unrecognisedScreens.Count > 0;

    public string UnrecognisedHeading => ShellText.UnrecognisedHeading;

    /// <summary>Replaces the tray's rows. Never opens the panel: the tray is there when the player looks.</summary>
    public void ShowUnrecognisedScreens(IReadOnlyList<V2UnrecognisedScreenViewModel> screens)
    {
        ArgumentNullException.ThrowIfNull(screens);
        void Apply()
        {
            _unrecognisedScreens = screens;
            OnPropertyChanged(nameof(UnrecognisedScreens));
            OnPropertyChanged(nameof(HasUnrecognisedScreens));
        }

        if (_dispatcherContext is null || ReferenceEquals(SynchronizationContext.Current, _dispatcherContext))
        {
            Apply();
        }
        else
        {
            _dispatcherContext.Post(_ => Apply(), null);
        }
    }
}
