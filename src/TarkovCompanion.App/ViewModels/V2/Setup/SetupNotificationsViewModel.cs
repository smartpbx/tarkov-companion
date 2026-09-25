using System.Windows.Input;
using TarkovCompanion.App.Localization;
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

    public string Title => SetupText.NotificationTitle(Kind);

    /// <summary>One plain sentence saying when this happens.</summary>
    public string Description => SetupText.NotificationDescription(Kind);

    /// <summary>
    /// Whether this one is allowed to interrupt a raid, said on the row rather than in a footnote.
    /// </summary>
    /// <remarks>
    /// The rule is the least obvious thing on this page and the most important: five of the six
    /// wait, and somebody who does not know that would read a silent evening as a broken switch.
    /// </remarks>
    public bool FiresDuringRaid => NotificationSamples.FiresDuringRaid(Kind);

    public string WhenLabel => FiresDuringRaid ? SetupText.NotificationsDuringRaid : SetupText.NotificationsAfterRaid;

    public bool IsEnabled
    {
        get => _isEnabled;
        private set => SetProperty(ref _isEnabled, value);
    }

    /// <summary>Shows what the bridge holds, without saving anything.</summary>
    internal void Show(bool enabled) => IsEnabled = enabled;

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
/// Setup › Notifications: the six, each with a switch and a way to see what it looks like.
/// </summary>
/// <remarks>
/// [V2 rough package 43] Several of these can only be seen by waiting for something to go wrong, so
/// every row carries a "Test this" button that sends the real notification through the real
/// channels. That is also the only way to find out whether the pop-up is on without playing.
/// </remarks>
public sealed class SetupNotificationsViewModel : BindableViewModel
{
    private readonly NotificationBridge? _bridge;
    private readonly Func<bool> _trayIsAvailable;
    private readonly Action<Action> _dispatch;
    private bool _showsPopup;
    private bool _quietHours;
    private int _quietFromHour;
    private int _quietToHour;

    /// <summary>[#902 P9] The last notifications, which the tray's "Notifications" opens.</summary>
    public RecentNotificationsViewModel Recent { get; }

    public SetupNotificationsViewModel(
        NotificationBridge? bridge,
        Func<bool>? trayIsAvailable = null,
        Action<Action>? dispatch = null)
    {
        _bridge = bridge;
        _dispatch = dispatch ?? (static action => action());
        // Read late: this page is composed before Avalonia has a tray to attach.
        _trayIsAvailable = trayIsAvailable ?? (static () => false);
        Recent = new RecentNotificationsViewModel(bridge, _dispatch);
        var settings = bridge?.Settings ?? NotificationSettings.Default;
        _showsPopup = settings.ShowsDesktopPopup;
        _quietHours = settings.QuietHours;
        _quietFromHour = Math.Clamp(settings.QuietFromHour, 0, 23);
        _quietToHour = Math.Clamp(settings.QuietToHour, 0, 23);
        Rows =
        [
            .. NotificationSamples.All.Select(kind => new SetupNotificationRowViewModel(
                kind,
                settings.IsEnabled(kind),
                SetEnabledAsync,
                Test)),
        ];
        TogglePopupCommand = new DelegateCommand(() => _ = SetPopupAsync(!ShowsPopup));
        ToggleQuietHoursCommand = new DelegateCommand(() =>
        {
            QuietHours = !QuietHours;
            _ = SaveQuietHoursAsync();
        });
        if (bridge is not null)
        {
            // #888: this page is built before the bridge has read notifications.json, so the copy
            // above is the defaults. Re-read whenever the bridge's settings change: after the load,
            // after an import or reset, and after every switch here.
            bridge.SettingsChanged += (_, _) => _dispatch(Refresh);
        }
    }

    /// <summary>
    /// Shows the bridge's current settings. Writes the backing fields directly, because the
    /// hour setters save, and showing a value is not choosing it.
    /// </summary>
    public void Refresh()
    {
        var settings = _bridge?.Settings ?? NotificationSettings.Default;
        ShowsPopup = settings.ShowsDesktopPopup;
        QuietHours = settings.QuietHours;
        if (SetField(ref _quietFromHour, Math.Clamp(settings.QuietFromHour, 0, 23)))
        {
            OnPropertyChanged(nameof(QuietFromHour));
        }

        if (SetField(ref _quietToHour, Math.Clamp(settings.QuietToHour, 0, 23)))
        {
            OnPropertyChanged(nameof(QuietToHour));
        }

        foreach (var row in Rows)
        {
            row.Show(settings.IsEnabled(row.Kind));
        }
    }

    private static bool SetField(ref int field, int value)
    {
        if (field == value)
        {
            return false;
        }

        field = value;
        return true;
    }

    public IReadOnlyList<SetupNotificationRowViewModel> Rows { get; }

    /// <summary>Whether this system has a tray at all, which is where these are delivered.</summary>
    public bool TrayIsAvailable => _trayIsAvailable();

    public string TrayStatusLine => TrayIsAvailable
        ? SetupText.NotificationsTrayAvailable
        : SetupText.NotificationsTrayMissing;

    /// <summary>
    /// Whether a notification may draw a window, which is off until asked for.
    /// </summary>
    public bool ShowsPopup
    {
        get => _showsPopup;
        private set => SetProperty(ref _showsPopup, value);
    }

    public string PopupTitle => SetupText.NotificationsPopupTitle;

    public string PopupDescription =>
        SetupText.NotificationsPopupDescription;

    public ICommand TogglePopupCommand { get; }

    public string QuietHoursTitle => SetupText.NotificationsQuietHoursTitle;

    public string QuietHoursDescription => SetupText.NotificationsQuietHoursDescription;

    /// <summary>Whether the pop-up keeps quiet between the two hours below.</summary>
    public bool QuietHours
    {
        get => _quietHours;
        private set => SetProperty(ref _quietHours, value);
    }

    /// <summary>"00:00" to "23:00", indexed by the hour.</summary>
    public IReadOnlyList<string> HourChoices { get; } =
        [.. Enumerable.Range(0, 24).Select(hour => hour.ToString("00", System.Globalization.CultureInfo.InvariantCulture) + ":00")];

    /// <summary>The hour quiet hours start, as an index into <see cref="HourChoices"/>.</summary>
    public int QuietFromHour
    {
        get => _quietFromHour;
        set
        {
            if (value is >= 0 and <= 23 && SetProperty(ref _quietFromHour, value))
            {
                _ = SaveQuietHoursAsync();
            }
        }
    }

    /// <summary>The hour quiet hours end, as an index into <see cref="HourChoices"/>.</summary>
    public int QuietToHour
    {
        get => _quietToHour;
        set
        {
            if (value is >= 0 and <= 23 && SetProperty(ref _quietToHour, value))
            {
                _ = SaveQuietHoursAsync();
            }
        }
    }

    public ICommand ToggleQuietHoursCommand { get; }

    private Task SaveQuietHoursAsync() =>
        _bridge?.SetQuietHoursAsync(QuietHours, QuietFromHour, QuietToHour, CancellationToken.None) ?? Task.CompletedTask;

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
