using TarkovCompanion.App.Localization;
using TarkovCompanion.App.Services.V2.Notifications;
using TarkovCompanion.Application.Services.Notifications;
using TarkovCompanion.Core.Common;

namespace TarkovCompanion.App.ViewModels.V2.Setup;

/// <summary>One notification as it was shown: when, and what it said.</summary>
public sealed record RecentNotificationRowViewModel(string TimeLabel, string Title, string Body)
{
    public bool HasBody => Body.Length > 0;
}

/// <summary>
/// Setup › Notifications › Recent: the last notifications, newest first, kept in memory only.
/// </summary>
/// <remarks>
/// [#902 P9] With the pop-up off, which is the default, a notification only raised a number in the
/// tray tooltip and nothing in the app said what it had been. The tray menu's "Notifications" opens
/// this list. Nothing is written to disk: a notification can carry an item name or a squadmate's
/// name, and a history of those is not something the player asked to keep.
/// </remarks>
public sealed class RecentNotificationsViewModel : BindableViewModel
{
    public const int Capacity = 20;

    private readonly Action<Action> _dispatch;
    private IReadOnlyList<RecentNotificationRowViewModel> _rows = [];

    public RecentNotificationsViewModel(NotificationBridge? bridge, Action<Action>? dispatch = null)
    {
        _dispatch = dispatch ?? (static action => action());
        if (bridge is not null)
        {
            bridge.Raised += (_, request) => _dispatch(() => Add(request));
        }
    }

    public IReadOnlyList<RecentNotificationRowViewModel> Rows
    {
        get => _rows;
        private set
        {
            if (SetProperty(ref _rows, value))
            {
                OnPropertyChanged(nameof(HasRows));
                OnPropertyChanged(nameof(HasNoRows));
            }
        }
    }

    public bool HasRows => Rows.Count > 0;

    public bool HasNoRows => !HasRows;

    public string Heading => SetupText.NotificationsRecentHeading;

    public string Empty => SetupText.NotificationsRecentEmpty;

    public void Add(NotificationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Rows =
        [
            new(LocalTime.ShortTime(request.RaisedUtc), request.Title, request.Body),
            .. Rows.Take(Capacity - 1),
        ];
    }
}
