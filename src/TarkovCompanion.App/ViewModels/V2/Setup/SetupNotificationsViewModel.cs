using System.Windows.Input;
using TarkovCompanion.App.Services.V2.Notifications;
using TarkovCompanion.Application.Services.Notifications;

namespace TarkovCompanion.App.ViewModels.V2.Setup;

/// <summary>One notification's row in Setup: what it is, whether it is on, and what it looks like.</summary>
public sealed class SetupNotificationRowViewModel : BindableViewModel
{
    private readonly Func<NotificationKind, bool, Task> _setEnabled;
    private bool _isEnabled;

    public SetupNotificationRowViewModel(
        NotificationKind kind,
        bool isEnabled,
        Func<NotificationKind, bool, Task> setEnabled,
        Action<NotificationKind> test)
    {
        ArgumentNullException.ThrowIfNull(setEnabled);
        ArgumentNullException.ThrowIfNull(test);
        Kind = kind;
        _isEnabled = isEnabled;
        _setEnabled = setEnabled;
        ToggleCommand = new DelegateCommand(() => _ = SetEnabledAsync(!IsEnabled));
        TestCommand = new DelegateCommand(() => test(kind));
    }

    public NotificationKind Kind { get; }

    public string Title => NotificationSamples.Title(Kind);

    /// <summary>One plain sentence saying when this happens.</summary>
    public string Description => NotificationSamples.Describe(Kind);

    /// <summary>
    /// Whether this one is allowed to interrupt a raid, said on the row rather than in a footnote.
    /// </summary>
    /// <remarks>
    /// The rule is the least obvious thing on this page and the most important: four of the five
    /// wait, and somebody who does not know that would read a silent evening as a broken switch.
    /// </remarks>
    public bool FiresDuringRaid => NotificationSamples.FiresDuringRaid(Kind);

    public string WhenLabel => FiresDuringRaid ? "During a raid too" : "After the raid";

    public bool IsEnabled
    {
        get => _isEnabled;
        private set => SetProperty(ref _isEnabled, value);
    }

    public string AutomationId => $"v2-setup-notification-{Kind.ToString().ToLowerInvariant()}";

    public string TestAutomationId => $"{AutomationId}-test";

    public ICommand ToggleCommand { get; }

    public ICommand TestCommand { get; }

    private async Task SetEnabledAsync(bool enabled)
    {
        IsEnabled = enabled;
        await _setEnabled(Kind, enabled).ConfigureAwait(true);
    }
}

/// <summary>
/// Setup › Notifications: the five, each with a switch and a way to see what it looks like.
/// </summary>
/// <remarks>
/// [V2 rough package 43] Four of these can only be seen by waiting for something to go wrong, so
/// every row carries a "Test this" button that sends the real notification through the real
/// channels. That is also the only way to find out whether the pop-up is on without playing.
/// </remarks>
public sealed class SetupNotificationsViewModel : BindableViewModel
{
    private readonly NotificationBridge? _bridge;
    private readonly Func<bool> _trayIsAvailable;
    private bool _showsPopup;

    public SetupNotificationsViewModel(NotificationBridge? bridge, Func<bool>? trayIsAvailable = null)
    {
        _bridge = bridge;
        // Read late: this page is composed before Avalonia has a tray to attach.
        _trayIsAvailable = trayIsAvailable ?? (static () => false);
        var settings = bridge?.Settings ?? NotificationSettings.Default;
        _showsPopup = settings.ShowsDesktopPopup;
        Rows =
        [
            .. NotificationSamples.All.Select(kind => new SetupNotificationRowViewModel(
                kind,
                settings.IsEnabled(kind),
                SetEnabledAsync,
                Test)),
        ];
        TogglePopupCommand = new DelegateCommand(() => _ = SetPopupAsync(!ShowsPopup));
    }

    public IReadOnlyList<SetupNotificationRowViewModel> Rows { get; }

    /// <summary>Whether this system has a tray at all, which is where these are delivered.</summary>
    public bool TrayIsAvailable => _trayIsAvailable();

    public string TrayStatusLine => TrayIsAvailable
        ? "The tray icon shows the raid state and counts anything you have not looked at."
        : "This system has no tray, so notifications are shown in the window only.";

    /// <summary>
    /// Whether a notification may draw a window, which is off until asked for.
    /// </summary>
    public bool ShowsPopup
    {
        get => _showsPopup;
        private set => SetProperty(ref _showsPopup, value);
    }

    public string PopupTitle => "Show a pop-up as well";

    public string PopupDescription =>
        "Off by default. The tray icon and its count never cover the game.";

    public ICommand TogglePopupCommand { get; }

    private Task SetEnabledAsync(NotificationKind kind, bool enabled) =>
        _bridge?.SetEnabledAsync(kind, enabled, CancellationToken.None) ?? Task.CompletedTask;

    private async Task SetPopupAsync(bool enabled)
    {
        ShowsPopup = enabled;
        if (_bridge is not null)
        {
            await _bridge.SetPopupAsync(enabled, CancellationToken.None).ConfigureAwait(true);
        }
    }

    private void Test(NotificationKind kind) => _bridge?.Test(kind);
}
