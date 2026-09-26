using TarkovCompanion.App.ViewModels;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Notifications;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.App.Services.V2.Notifications;

/// <summary>
/// Turns everything the application already knows into the six notifications, and hands them out.
/// </summary>
/// <remarks>
/// <para>
/// [V2 rough package 43] The one place that joins the parts: the runtime state store says where
/// the player is and what the relay is doing, the settings view model says whether a build is
/// waiting, the stored settings say which notifications are wanted, and
/// <see cref="NotificationCoordinator"/> decides. Nothing here has an opinion of its own — moving
/// a rule into this class would be moving it out of reach of a test.
/// </para>
/// <para>
/// It observes on every state change and on a tick, because a coalesced burst of marks has to come
/// out even when nothing else happens for the next few seconds.
/// </para>
/// </remarks>
public sealed class NotificationBridge : IDisposable
{
    /// <summary>How often a gathered burst is given a chance to come out.</summary>
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);

    private readonly IRuntimeStateStore _stateStore;
    private readonly INotificationSettingsStore _settingsStore;
    private readonly IGroupSettingsStore _groupSettings;
    private readonly NotificationCoordinator _coordinator;
    private readonly IReadOnlyList<INotificationChannel> _quietChannels;
    private readonly Func<INotificationChannel?> _popupChannel;
    private readonly INativeNotificationChannel? _nativePopupChannel;
    private readonly Func<SettingsPageViewModel?> _settingsPage;
    private readonly TimeProvider _timeProvider;
    private readonly ITimer _flushTimer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NotificationSettings _settings = NotificationSettings.Default;
    private string? _playerName;
    private bool _disposed;

    public NotificationBridge(
        IRuntimeStateStore stateStore,
        INotificationSettingsStore settingsStore,
        IGroupSettingsStore groupSettings,
        IReadOnlyList<INotificationChannel> quietChannels,
        // Resolved late and per notification, because the window it draws into may not exist yet
        // and may be closed to the tray by the time one arrives.
        Func<INotificationChannel?> popupChannel,
        // A function, not the view model: resolving it eagerly here would build the whole V1
        // graph the moment notifications are composed.
        Func<SettingsPageViewModel?>? settingsPage = null,
        TimeProvider? timeProvider = null,
        NotificationCoordinator? coordinator = null,
        INativeNotificationChannel? nativePopupChannel = null)
    {
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _groupSettings = groupSettings ?? throw new ArgumentNullException(nameof(groupSettings));
        _quietChannels = quietChannels ?? throw new ArgumentNullException(nameof(quietChannels));
        _popupChannel = popupChannel ?? throw new ArgumentNullException(nameof(popupChannel));
        _nativePopupChannel = nativePopupChannel;
        _settingsPage = settingsPage ?? (static () => null);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _coordinator = coordinator ?? new NotificationCoordinator();
        _stateStore.Changed += StateChanged;
        _flushTimer = _timeProvider.CreateTimer(_ => Observe(), null, FlushInterval, FlushInterval);
    }

    /// <summary>Raised after a notification has been handed to every channel, for the tray count.</summary>
    public event EventHandler<NotificationRequest>? Raised;

    /// <summary>
    /// Raised after <see cref="Settings"/> changes: once the stored file is loaded, and after every
    /// save. May be raised off the UI thread.
    /// </summary>
    /// <remarks>
    /// #888: Setup › Notifications is composed before <see cref="InitializeAsync"/> runs, so a copy
    /// taken in its constructor was always the defaults. The page showed the pop-up off for a
    /// player who had turned it on, and its quiet-hours toggle wrote the stale 23-8 over a saved
    /// 22-7. The page re-reads on this event instead.
    /// </remarks>
    public event EventHandler? SettingsChanged;

    /// <summary>Which notifications are currently on.</summary>
    public NotificationSettings Settings => _settings;

    /// <summary>Loads the stored settings and the player's own relay name.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _settings = await _settingsStore.GetAsync(cancellationToken).ConfigureAwait(false);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
        await RefreshPlayerNameAsync(cancellationToken).ConfigureAwait(false);
        Observe();
    }

    /// <summary>Turns one notification on or off, and remembers the answer.</summary>
    public async Task SetEnabledAsync(NotificationKind kind, bool enabled, CancellationToken cancellationToken)
    {
        await ApplyAsync(_settings.With(kind, enabled), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Turns the desktop pop-up on or off, and remembers the answer.</summary>
    public async Task SetPopupAsync(bool enabled, CancellationToken cancellationToken)
    {
        await ApplyAsync(_settings with { ShowsDesktopPopup = enabled }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Replaces every notification setting at once and saves them in one write, for a settings
    /// import or reset.
    /// </summary>
    public async Task ReplaceAsync(NotificationSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await ApplyAsync(settings with
        {
            QuietFromHour = Math.Clamp(settings.QuietFromHour, 0, 23),
            QuietToHour = Math.Clamp(settings.QuietToHour, 0, 23),
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Shows what one notification looks like, now, whether or not its condition has happened.
    /// </summary>
    /// <remarks>
    /// Goes through the real channels rather than a preview, because the question the button
    /// answers is "what will I actually see". It ignores the switch beside it on purpose: pressing
    /// "test this" on a notification you have turned off is how you decide whether to turn it on.
    /// Quiet hours are ignored too: a test pressed at midnight is somebody asking to see it.
    /// </remarks>
    public void Test(NotificationKind kind) => Deliver(TarkovCompanion.App.Localization.SetupText.NotificationSample(kind, _timeProvider.GetUtcNow()), ignoreQuietHours: true);

    /// <summary>Sets quiet hours for the pop-up, and remembers the answer.</summary>
    public async Task SetQuietHoursAsync(bool enabled, int fromHour, int toHour, CancellationToken cancellationToken)
    {
        await ApplyAsync(_settings with
        {
            QuietHours = enabled,
            QuietFromHour = Math.Clamp(fromHour, 0, 23),
            QuietToHour = Math.Clamp(toHour, 0, 23),
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether the pop-up is being held back by quiet hours right now.</summary>
    /// <remarks>
    /// Read in <see cref="LocalTime.Zone"/>, the zone every time on screen is shown in (#888). The
    /// player picks the hours against those times; the machine's zone differs from it whenever the
    /// profile names another zone, and quiet hours then ran hours off what the player chose.
    /// </remarks>
    public bool IsQuietNow() => _settings.IsQuietAt(
        TimeOnly.FromDateTime(LocalTime.ToLocal(_timeProvider.GetUtcNow()).DateTime));

    /// <summary>Re-reads the player's own relay name, so their own marks stay silent.</summary>
    public async Task RefreshPlayerNameAsync(CancellationToken cancellationToken)
    {
        try
        {
            var stored = await _groupSettings.GetAsync(cancellationToken).ConfigureAwait(false);
            _playerName = stored.DisplayName;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Without a name every relay mark counts as somebody else's, which is noisier than it
            // should be but never silences a real one. Failing the other way would hide them all.
            _playerName = null;
        }
    }

    private async Task ApplyAsync(NotificationSettings settings, CancellationToken cancellationToken)
    {
        _settings = settings;
        await _settingsStore.SaveAsync(_settings, cancellationToken).ConfigureAwait(false);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stateStore.Changed -= StateChanged;
        _flushTimer.Dispose();
        _gate.Dispose();
    }

    private void StateChanged(object? sender, EventArgs e) => Observe();

    private void Observe()
    {
        if (_disposed || !_gate.Wait(0))
        {
            return;
        }

        try
        {
            foreach (var request in _coordinator.Observe(BuildInputs(_stateStore.Current), _settings))
            {
                Deliver(request);
            }
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            try
            {
                _gate.Release();
            }
            catch (ObjectDisposedException)
            {
                // Disposed while a timer tick was inside; that tick crashed the whole test host.
            }
        }
    }

    private void Deliver(NotificationRequest request, bool ignoreQuietHours = false)
    {
        foreach (var channel in _quietChannels)
        {
            channel.Show(request);
        }

        // The one thing that can put something on screen by itself, and the one thing that is off
        // until asked for.
        if (_settings.ShowsDesktopPopup && (ignoreQuietHours || !IsQuietNow()))
        {
            if (_nativePopupChannel is { IsAvailable: true } native)
            {
                // Windows may retain this text in Notification Center and show it on a lock
                // screen. It therefore gets the deliberately detail-free projection, and unlike
                // the in-window fallback it never interrupts a loading or active raid.
                if (!IsRaidActive())
                {
                    native.Show(NotificationPrivacy.ForLockScreen(request));
                }
            }
            else
            {
                _popupChannel()?.Show(request);
            }
        }

        Raised?.Invoke(this, request);
    }

    private bool IsRaidActive() =>
        _stateStore.Current.Raid.State is RaidLifecycleState.LoadingRaid or RaidLifecycleState.InRaid;

    /// <summary>Internal for direct coverage: what the coordinator is actually shown.</summary>
    internal NotificationInputs BuildInputs(ApplicationRuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var group = snapshot.Group;
        // Waypoints and pings are one thing to somebody being shot at. The relay's own ids make
        // them distinguishable from each other across ticks; they are offset so a waypoint and a
        // ping that happen to share an id are not treated as the same mark.
        var marks = group.Waypoints
            .Select(waypoint => new SquadMarkInput(waypoint.Id * 2, waypoint.By, IsPing: false))
            .Concat(group.Pings.Select(ping => new SquadMarkInput((ping.Id * 2) + 1, ping.By, IsPing: true)))
            .ToArray();
        return new()
        {
            NowUtc = _timeProvider.GetUtcNow(),
            RaidState = snapshot.Raid.State,
            RaidId = snapshot.Raid.RaidId,
            PlayerName = _playerName,
            IsSharing = group.IsSharing,
            RelayStaleSince = group.StaleSince,
            SquadMarks = marks,
            FleaSales = snapshot.FleaSales.Sales
                .Select(sale => new FleaSaleInput(sale.SaleKey, sale.Count, sale.WrittenUtc))
                .ToArray(),
            FailedDataEndpoints = snapshot.Data.FailedEndpoints,
            // "Ready to install" is the unpacked build waiting for a restart, not merely one that
            // exists in the feed: an offer somebody has not downloaded is not ready for anything.
            UpdateReadyBuild = _settingsPage() is { CanRestartForUpdate: true } settings
                ? settings.AvailableBuild
                : null,
        };
    }
}
