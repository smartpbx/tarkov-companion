using System.Windows.Input;
using TarkovCompanion.App.Localization;
using TarkovCompanion.App.Services.V2.Shell;

namespace TarkovCompanion.App.ViewModels.V2.Shell;

/// <summary>
/// "Update ready · Restart": a waiting build said in words, with the one press that installs it.
/// </summary>
/// <remarks>
/// [#881] "The settings gear is cut off … this makes the notification of a new update very quiet
/// too." The only sign of a waiting build was an 8 px dot on the gear, and on Clayton's screen the
/// gear itself was half under the taskbar. A dot is easy to miss even when it is fully drawn, so
/// the shell now also says it, above the gear, with the button that acts on it.
///
/// Dismissing hides it for this build only. When the waiting state clears (installed, or the feed
/// withdrew it) and a build waits again, it shows again: a later build is new news. The dot on the
/// gear stays either way, so dismissing never hides the fact that an update is waiting.
/// </remarks>
public sealed class V2UpdateReadyNoticeViewModel : BindableViewModel, IDisposable
{
    private readonly IUpdateWaitingSource? _source;
    private readonly Func<string?> _version;
    private bool _waiting;
    private bool _dismissed;

    public V2UpdateReadyNoticeViewModel(IUpdateWaitingSource? source, ICommand? install, Func<string?>? version = null)
    {
        _source = source;
        _version = version ?? (() => null);
        InstallCommand = install;
        DismissCommand = new DelegateCommand(Dismiss);
        if (_source is not null)
        {
            // Read before subscribing: the event fires only on a change, and the updater may have
            // found the build before this shell existed.
            _waiting = _source.IsUpdateWaiting;
            _source.UpdateWaitingChanged += OnWaitingChanged;
        }
    }

    /// <summary>Whether the notice is on screen.</summary>
    public bool IsVisible => _waiting && !_dismissed && InstallCommand is not null;

    public string Label => ShellText.UpdateReady;

    public string ActionLabel => ShellText.UpdateReadyRestart;

    public string DismissLabel => ShellText.UpdateReadyDismiss;

    /// <summary>The tooltip and accessible name: the version when the feed named one.</summary>
    public string Description => _version() is { Length: > 0 } version && char.IsDigit(version[0])
        ? ShellText.UpdateReadyVersion(version)
        : ShellText.UpdateReady;

    public ICommand? InstallCommand { get; }

    public ICommand DismissCommand { get; }

    public void Dismiss()
    {
        if (_dismissed) return;
        _dismissed = true;
        OnPropertyChanged(nameof(IsVisible));
    }

    /// <summary>Render-only: draws the notice as if a build were waiting (no updater runs in a render).</summary>
    internal void PresentForPreview()
    {
        _waiting = true;
        OnPropertyChanged(nameof(IsVisible));
    }

    public void Dispose()
    {
        if (_source is not null) _source.UpdateWaitingChanged -= OnWaitingChanged;
    }

    private void OnWaitingChanged(object? sender, bool waiting)
    {
        if (!waiting) _dismissed = false;
        _waiting = waiting;
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(Description));
    }
}
