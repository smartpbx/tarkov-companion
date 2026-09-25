using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.Application.Services.Notifications;
using TarkovCompanion.Application.Services.Runtime;

namespace TarkovCompanion.App.Services.V2.Notifications;

/// <summary>What the tray menu can do, supplied by whoever owns the window.</summary>
/// <param name="Show">Bring the window back and focus it.</param>
/// <param name="OpenRaid">Show the window on the Raid workspace.</param>
/// <param name="OpenTeam">Show the window on Team.</param>
/// <param name="OpenSetup">Show the window on Setup.</param>
/// <param name="Quit">Really quit, as opposed to closing the window.</param>
/// <param name="OpenNotifications">[#902 P9] Show the window on the recent notifications list.</param>
public sealed record TrayPresenceActions(
    Action Show,
    Action OpenRaid,
    Action OpenTeam,
    Action OpenSetup,
    Action Quit,
    Action? OpenNotifications = null);

/// <summary>
/// The companion's presence in the system tray: what it is doing, and the way back into it.
/// </summary>
/// <remarks>
/// <para>
/// [V2 rough package 43] Clayton plays with the companion on a second screen and is not looking at
/// it. The tray is the one surface that can say something without taking the screen: the icon
/// carries the raid state, the tooltip carries the detail and the unread count, and the menu is
/// how you get back to the window after closing it.
/// </para>
/// <para>
/// Everything here is best-effort and degrades to nothing. A platform with no tray (a headless CI
/// host, a bare Linux session) leaves <see cref="IsAvailable"/> false, and the window then closes
/// the way it always did rather than vanishing into a tray that is not there. That is also why the
/// icon is composed inside a try/catch: drawing needs a rendering platform, and the fallback ladder
/// is the state-coloured icon, then the plain application icon, then no tray at all.
/// </para>
/// </remarks>
public sealed class TrayPresence : INotificationChannel, IDisposable
{
    private static readonly Uri BaseIconUri = new("avares://TarkovCompanion/Assets/TarkovCompanion.png");

    private readonly TrayPresenceActions _actions;
    private readonly Dictionary<TrayStatus, WindowIcon> _icons = [];
    private readonly TrayIcon? _tray;
    private readonly Bitmap? _baseIcon;
    private TrayStatus _status = TrayStatus.Idle;
    private NativeMenuItem? _notificationsItem;
    private int _unread;
    private bool _disposed;

    public TrayPresence(Avalonia.Application application, TrayPresenceActions actions)
    {
        ArgumentNullException.ThrowIfNull(application);
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        // Windows only, the way the rest of the platform layer draws its line. A tray exists on
        // other desktops, but this is the platform the companion is used on and the one the
        // close-to-tray behaviour is verified on; everywhere else the window keeps closing the way
        // it always has rather than disappearing into something that may not be there.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            _baseIcon = new Bitmap(AssetLoader.Open(BaseIconUri));
            _tray = new TrayIcon
            {
                ToolTipText = "Tarkov Companion",
                Icon = IconFor(TrayStatus.Idle),
                IsVisible = true,
                Menu = BuildMenu(),
            };
            _tray.Clicked += (_, _) => _actions.Show();
            TrayIcon.SetIcons(application, [_tray]);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // No tray on this platform, or nothing to draw with. The application is unchanged.
            _tray?.Dispose();
            _tray = null;
        }
    }

    /// <summary>Whether a tray icon actually exists, which is what closing to tray depends on.</summary>
    public bool IsAvailable => _tray is not null;

    /// <summary>How many notifications have arrived since the window was last looked at.</summary>
    public int Unread => _unread;

    /// <summary>Restates the icon and tooltip from the current state of everything.</summary>
    public void Update(ApplicationRuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_tray is null || _disposed)
        {
            return;
        }

        var text = TrayPresenceState.Describe(snapshot, _unread);
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || _tray is null)
            {
                return;
            }

            _tray.ToolTipText = text.Tooltip;
            if (text.Status != _status && IconFor(text.Status) is { } icon)
            {
                _status = text.Status;
                _tray.Icon = icon;
            }
        });
    }

    /// <summary>
    /// A notification arrived: the tray counts it. It does not open anything.
    /// </summary>
    /// <remarks>
    /// This is the quiet channel, and it is the default one. Whether anything is also drawn on
    /// screen is the pop-up setting's business, not the tray's.
    /// </remarks>
    public void Show(NotificationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _unread += Math.Max(1, request.Count);
        RelabelNotifications();
    }

    /// <summary>The window came back, so the count has been read.</summary>
    public void ClearUnread()
    {
        _unread = 0;
        RelabelNotifications();
    }

    /// <summary>"Notifications (3)": the count the tooltip carries, where it can be opened.</summary>
    private void RelabelNotifications()
    {
        if (_notificationsItem is not { } item || _disposed)
        {
            return;
        }

        var header = TarkovCompanion.App.Localization.ShellText.TrayNotifications(_unread);
        Dispatcher.UIThread.Post(() => item.Header = header);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _tray?.Dispose();
        _icons.Clear();
        // Last, because the fallback icons hand it straight to the platform rather than copying it.
        _baseIcon?.Dispose();
    }

    private NativeMenu BuildMenu()
    {
        var menu = new NativeMenu();
        foreach (var (header, action) in new (string, Action)[]
                 {
                     // [#902 P6] The rail's own words, from the string table like the rest of the app.
                     (TarkovCompanion.App.Localization.UiText.Get("Shell.Tray.Show"), _actions.Show),
                     (TarkovCompanion.App.Localization.UiText.Get("Shell.Label.Raid"), _actions.OpenRaid),
                     (TarkovCompanion.App.Localization.UiText.Get("Shell.Label.Team"), _actions.OpenTeam),
                     (TarkovCompanion.App.Localization.UiText.Get("Shell.Label.Setup"), _actions.OpenSetup),
                 })
        {
            var item = new NativeMenuItem(header);
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        if (_actions.OpenNotifications is { } openNotifications)
        {
            // [#902 P9] The tray counted notifications with nowhere to read them.
            _notificationsItem = new NativeMenuItem(TarkovCompanion.App.Localization.ShellText.TrayNotifications(_unread));
            _notificationsItem.Click += (_, _) => openNotifications();
            menu.Items.Add(_notificationsItem);
        }

        menu.Items.Add(new NativeMenuItemSeparator());
        var quit = new NativeMenuItem(TarkovCompanion.App.Localization.UiText.Get("Shell.Tray.Quit"));
        quit.Click += (_, _) => _actions.Quit();
        menu.Items.Add(quit);
        return menu;
    }

    /// <summary>
    /// The application icon with a coloured dot for the state, or the plain icon if it cannot be drawn.
    /// </summary>
    private WindowIcon? IconFor(TrayStatus status)
    {
        if (_icons.TryGetValue(status, out var cached))
        {
            return cached;
        }

        if (_baseIcon is null)
        {
            return null;
        }

        WindowIcon icon;
        try
        {
            const int extent = 32;
            var surface = new RenderTargetBitmap(new PixelSize(extent, extent));
            using (var context = surface.CreateDrawingContext())
            {
                context.DrawImage(
                    _baseIcon,
                    new Rect(0, 0, _baseIcon.Size.Width, _baseIcon.Size.Height),
                    new Rect(0, 0, extent, extent));
                if (DotFor(status) is { } colour)
                {
                    context.DrawEllipse(
                        new SolidColorBrush(colour),
                        new Pen(new SolidColorBrush(Color.FromRgb(0x0B, 0x10, 0x16)), 2),
                        new Point(extent - 8, extent - 8),
                        6,
                        6);
                }
            }

            icon = new WindowIcon(surface);
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            // No rendering platform: the plain application icon still says which application this
            // is, which is most of what a tray icon is for.
            icon = new WindowIcon(_baseIcon);
        }

        _icons[status] = icon;
        return icon;
    }

    /// <summary>
    /// The one colour the icon carries. Red means only "something needs you".
    /// </summary>
    private static Color? DotFor(TrayStatus status) => status switch
    {
        TrayStatus.InRaid => Color.FromRgb(0x4C, 0xC9, 0xE0),
        TrayStatus.Loading => Color.FromRgb(0xE0, 0xA3, 0x5C),
        TrayStatus.PostRaid => Color.FromRgb(0x7A, 0xC9, 0x8A),
        TrayStatus.Attention => Color.FromRgb(0xE0, 0x5C, 0x5C),
        _ => null,
    };
}
