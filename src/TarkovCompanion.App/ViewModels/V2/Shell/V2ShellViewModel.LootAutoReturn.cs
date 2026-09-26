using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels.V2.LootScan;
using TarkovCompanion.App.ViewModels.V2.Setup;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.App.ViewModels.V2.Shell;

/// <summary>
/// #572: a Loot page the app opened by itself, mid-raid, closes itself too - back to the Raid
/// map - instead of leaving somebody whose companion sits on a second screen stuck alt-tabbing to
/// swap it back by hand every time. <see cref="LootAutoReturnPolicy"/> is the rule; this file is
/// only the wiring: which of the several ways to reach the Loot route count as "by itself" versus
/// "by hand", where the once-a-second check comes from, and how a non-loot screenshot or the
/// page's own "Stay" toggle reach the policy.
/// </summary>
public sealed partial class V2ShellViewModel
{
    private LootAutoReturnPolicy _lootAutoReturn = null!;
    private bool _lootAutoReturnNavigationInFlight;
    private SetupLootScanViewModel? _lootScanSettings;

    /// <summary>
    /// [#572] Setup's "Show loot results on the tablet only", while a tablet is paired: the
    /// result goes to the tablet and the desktop is left on whatever it was showing (the map).
    /// </summary>
    internal bool LootGoesToTabletOnly =>
        _lootScanSettings?.TabletOnly == true &&
        _companionPairing?.Devices.Any(device => device.CanRevoke) == true;

    private bool IsInRaid => _runtime.Current.Raid.State == RaidLifecycleState.InRaid;

    /// <summary>The configured countdown; null means the timed return is switched off.</summary>
    public TimeSpan? LootAutoReturnTimeout => _lootAutoReturn.Timeout;

    private void WireLootAutoReturn()
    {
        _lootAutoReturn = new LootAutoReturnPolicy(_clock);
        Router.Navigated += LootAutoReturnRouterNavigated;
    }

    private void UnwireLootAutoReturn()
    {
        Router.Navigated -= LootAutoReturnRouterNavigated;
        if (Volatile.Read(ref _lootScanResult) is { } current)
        {
            current.StayToggled -= LootAutoReturnStayToggled;
        }
    }

    /// <summary>[#572] The Loot page's per-stage progress line; null in a shell built without Setup's admin pages.</summary>
    public LootScanProgressViewModel? LootScanProgress { get; private set; }

    /// <summary>
    /// [#572] Setup's remembered countdown, applied now and whenever the player picks another,
    /// and the progress line the Loot page shows while a scan runs.
    /// </summary>
    private void AttachLootScanSettings(SetupLootScanViewModel? settings)
    {
        if (settings is null)
        {
            return;
        }

        _lootScanSettings = settings;
        LootScanProgress = settings.Progress;
        OnPropertyChanged(nameof(LootScanProgress));
        SetLootAutoReturnTimeout(settings.Timeout);
        settings.TimeoutChanged += SetLootAutoReturnTimeout;
    }

    /// <summary>Setup's countdown control: null, zero or negative turns the timed return off.</summary>
    public void SetLootAutoReturnTimeout(TimeSpan? timeout)
    {
        _lootAutoReturn.Configure(timeout);
        PushLootAutoReturnState();
    }

    /// <summary>
    /// A screenshot the capture pipeline read as something other than a loot container: the
    /// player is moving again. A no-op unless the Loot page is showing an automatic, unpinned
    /// result mid-raid - see <see cref="LootAutoReturnPolicy.NonLootScreenshot"/>. Reaches this
    /// from whatever thread the capture handoff finished on, so it takes the same dispatcher-post
    /// dance <see cref="ShowLootScanResult"/> does.
    /// </summary>
    public void ReportNonLootScreenshot()
    {
        void Apply()
        {
            if (_lootAutoReturn.NonLootScreenshot())
            {
                ReturnFromLootToMap();
            }
            else
            {
                PushLootAutoReturnState();
            }
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

    /// <summary>
    /// #572: navigates to Loot the moment a capture is accepted, long before grid reconstruction,
    /// profile lookup and recommendation finish - which measured 350 ms to well over a second.
    /// Whatever was on screen (a previous result, or the empty "no scan yet" state) stays up until
    /// <see cref="ShowLootScanResult"/> replaces it; nothing here clears it, so the page is never
    /// blanked while the new one computes. A no-op once already on Loot, auto-entered: a second
    /// loot screenshot's own EnterLoot call (from ShowLootScanResult) is what restarts the wait,
    /// and duplicating that here would double-count it.
    /// </summary>
    public void ShowLootScanStarting()
    {
        void Apply()
        {
            if (Router.Current.Location.Route == V2Routes.Loot || LootGoesToTabletOnly || LootStaysOnNowPanel)
            {
                return;
            }

            _lootAutoReturnNavigationInFlight = true;
            try
            {
                GoTo(V2Routes.Loot);
            }
            finally
            {
                _lootAutoReturnNavigationInFlight = false;
            }

            _lootAutoReturn.EnterLoot(handEntered: false, inRaid: IsInRaid);
            PushLootAutoReturnState();
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

    /// <summary>
    /// The one path a capture result reaches the Loot page by itself
    /// (<see cref="ShowLootScanResult"/>). Runs whether or not the route actually changes: a
    /// second loot screenshot re-decides the same page in place, and that has to restart the wait
    /// exactly as a fresh entry would.
    /// </summary>
    private void EnterLootAutomatically(LootScanViewModel result)
    {
        if (Volatile.Read(ref _lootScanResult) is { } previous && !ReferenceEquals(previous, result))
        {
            previous.StayToggled -= LootAutoReturnStayToggled;
        }

        result.StayToggled -= LootAutoReturnStayToggled;
        result.StayToggled += LootAutoReturnStayToggled;
        if (LootGoesToTabletOnly || LootStaysOnNowPanel) // [#712 0-4] decision 7: the verdict stays in LAST SCAN
        {
            // The result is held for the Loot page, which the player can still open by hand.
            PushLootAutoReturnState();
            return;
        }

        _lootAutoReturnNavigationInFlight = true;
        try
        {
            GoTo(V2Routes.Loot);
        }
        finally
        {
            _lootAutoReturnNavigationInFlight = false;
        }

        _lootAutoReturn.EnterLoot(handEntered: false, inRaid: IsInRaid);
        PushLootAutoReturnState();
    }

    /// <summary>
    /// Every navigation, including the one <see cref="EnterLootAutomatically"/> just made. Reached
    /// Loot any other way - the nav bar, a typed address, a Continue tile, Back/Forward - counts
    /// as by hand, which the policy never moves.
    /// </summary>
    private void LootAutoReturnRouterNavigated(object? sender, V2NavigationChange change)
    {
        var enteredLoot = change.Current.Location.Route == V2Routes.Loot;
        var leftLoot = !enteredLoot && change.Previous.Location.Route == V2Routes.Loot;

        if (leftLoot)
        {
            _lootAutoReturn.Leave();
        }

        if (enteredLoot && !_lootAutoReturnNavigationInFlight)
        {
            _lootAutoReturn.EnterLoot(handEntered: true, inRaid: IsInRaid);
        }

        PushLootAutoReturnState();
    }

    private void LootAutoReturnStayToggled(object? sender, bool stay)
    {
        _lootAutoReturn.Stay(stay);
        PushLootAutoReturnState();
    }

    /// <summary>The once-a-second header tick doubles as this page's own clock.</summary>
    private void TickLootAutoReturn()
    {
        if (_lootAutoReturn.Tick())
        {
            ReturnFromLootToMap();
            return;
        }

        PushLootAutoReturnState();
    }

    private void ReturnFromLootToMap()
    {
        if (Router.Current.Location.Route == V2Routes.Loot)
        {
            GoTo(V2Routes.Raid);
        }
    }

    private void PushLootAutoReturnState()
    {
        if (Volatile.Read(ref _lootScanResult) is { } current)
        {
            current.UpdateAutoReturn(
                _lootAutoReturn.IsEligible,
                _lootAutoReturn.ShowsCountdown,
                _lootAutoReturn.IsPinned,
                _lootAutoReturn.Remaining);
        }
    }
}
