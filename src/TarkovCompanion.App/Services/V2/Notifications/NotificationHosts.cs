using Avalonia.Controls.Notifications;
using Avalonia.Threading;
using TarkovCompanion.Application.Services.Notifications;
using TarkovCompanion.Application.Services.Runtime;

namespace TarkovCompanion.App.Services.V2.Notifications;

/// <summary>
/// Holds the tray, which cannot exist until Avalonia has started.
/// </summary>
/// <remarks>
/// [V2 rough package 43] The composition graph is built before there is an
/// <see cref="Avalonia.Application"/> to hang a tray icon on, and the notification bridge needs
/// somewhere to deliver to from the moment it starts. This is that somewhere: registered early,
/// attached when the framework is up, and a no-op in between and on any platform without a tray.
/// </remarks>
public sealed class TrayPresenceHost : INotificationChannel, IDisposable
{
    private TrayPresence? _tray;

    public bool IsAvailable => _tray?.IsAvailable ?? false;

    public int Unread => _tray?.Unread ?? 0;

    public void Attach(Avalonia.Application application, TrayPresenceActions actions)
    {
        _tray?.Dispose();
        _tray = new(application, actions);
    }

    public void Update(ApplicationRuntimeSnapshot snapshot) => _tray?.Update(snapshot);

    public void Show(NotificationRequest request) => _tray?.Show(request);

    public void ClearUnread() => _tray?.ClearUnread();

    public void Dispose()
    {
        _tray?.Dispose();
        _tray = null;
    }
}

/// <summary>
/// Holds the window a pop-up would be drawn in, when there is one and the player asked for it.
/// </summary>
/// <remarks>
/// This is an in-window toast rather than a desktop toast, and that is the point: it cannot appear
/// over a full-screen game, because it is inside a window the game is covering. Somebody with the
/// companion on a second monitor sees it; somebody mid-fight on one monitor does not.
/// </remarks>
public sealed class PopupNotificationHost : INotificationChannel
{
    private WindowNotificationManager? _manager;

    public void Attach(WindowNotificationManager manager) => _manager = manager;

    public void Show(NotificationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_manager is not { } manager)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => manager.Show(new Notification(
            request.Title,
            request.Body,
            request.Kind == NotificationKind.SquadMark
                ? NotificationType.Information
                : NotificationType.Warning)));
    }
}
