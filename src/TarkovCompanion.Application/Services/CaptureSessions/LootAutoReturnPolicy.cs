namespace TarkovCompanion.Application.Services.CaptureSessions;

/// <summary>
/// Decides when the shell leaves the Loot page by itself and returns to the Raid map (#572).
/// </summary>
/// <remarks>
/// The owner plays with the app on a second screen and cannot click it mid-raid: a Loot result
/// that the app opened by itself must close itself too, or he has to alt-tab and swap manually
/// every single time. This is pixel-free and clock-free by construction - every fact it needs
/// (that a loot result was shown, that the app is mid-raid, that a screenshot just arrived and
/// what it was) is handed in by the caller, and "now" comes from an injected
/// <see cref="TimeProvider"/> so a test can move time without a real wait.
///
/// A player who opened Loot by hand - the nav bar, a typed address, a Continue tile, Back/Forward
/// - is never moved: the rule exists to undo a navigation the app made for itself, never one the
/// player made on purpose. "Stay" pins the page until it is actually left
/// (<see cref="Leave"/>), which is what "for the rest of that raid" means in practice: a fresh
/// loot screenshot re-decides the same page in place and must not un-pin it.
/// </remarks>
public sealed class LootAutoReturnPolicy(TimeProvider? clock = null)
{
    /// <summary>The shortest configurable countdown.</summary>
    public const int MinimumSeconds = 5;

    /// <summary>The longest configurable countdown.</summary>
    public const int MaximumSeconds = 60;

    /// <summary>What a fresh install ships with.</summary>
    public const int DefaultSeconds = 15;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private TimeSpan? _timeout = TimeSpan.FromSeconds(DefaultSeconds);
    private bool _autoEntered;
    private bool _inRaid;
    private bool _pinned;
    private DateTimeOffset? _deadlineUtc;

    /// <summary>The configured countdown; null means the timed return is switched off.</summary>
    public TimeSpan? Timeout => _timeout;

    /// <summary>
    /// Whether this page is one the app opened for itself, mid-raid - the condition under which
    /// "Stay" is worth offering at all, whether or not it is currently pinned or counting down.
    /// </summary>
    public bool IsEligible => _autoEntered && _inRaid;

    /// <summary>
    /// Whether this is a page the app opened for itself, mid-raid, and the player has not pinned
    /// it open. False for a hand-opened page, one opened outside a raid, or a pinned one - in
    /// every one of those cases nothing here may move the player anywhere.
    /// </summary>
    public bool IsActive => IsEligible && !_pinned;

    /// <summary>Whether the countdown badge should be on screen: active, and not switched off.</summary>
    public bool ShowsCountdown => IsActive && _timeout is not null;

    /// <summary>Whether the player pinned the page open with "Stay".</summary>
    public bool IsPinned => _pinned;

    /// <summary>Time left before the timed return fires, or null while none is running.</summary>
    public TimeSpan? Remaining
    {
        get
        {
            if (_deadlineUtc is not { } deadline)
            {
                return null;
            }

            var left = deadline - _clock.GetUtcNow();
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Sets the configured countdown. Null, zero or negative turns the timed return off; anything
    /// else is clamped to [<see cref="MinimumSeconds"/>, <see cref="MaximumSeconds"/>]. Restarts
    /// the wait if a countdown is currently running.
    /// </summary>
    public void Configure(TimeSpan? timeout)
    {
        _timeout = timeout is { } value && value > TimeSpan.Zero
            ? TimeSpan.FromSeconds(Math.Clamp(value.TotalSeconds, MinimumSeconds, MaximumSeconds))
            : null;
        RestartDeadline();
    }

    /// <summary>
    /// The shell just showed the Loot page from a fresh scan. <paramref name="handEntered"/> is
    /// true for every path except a capture result the app produced by itself (the nav bar, a
    /// typed address, a Continue tile, Back/Forward all count as by hand); a hand entry, or one
    /// outside a raid, never starts a countdown. Deliberately leaves <see cref="IsPinned"/> alone:
    /// this is also how a second loot screenshot re-decides the same page in place, and that must
    /// not clear a "Stay" the player already chose for this raid.
    /// </summary>
    public void EnterLoot(bool handEntered, bool inRaid)
    {
        _autoEntered = !handEntered;
        _inRaid = inRaid;
        RestartDeadline();
    }

    /// <summary>The shell left the Loot page, however it happened. Clears everything, including the pin.</summary>
    public void Leave()
    {
        _autoEntered = false;
        _pinned = false;
        _deadlineUtc = null;
    }

    /// <summary>
    /// Tracks the raid lifecycle. An auto-entered page whose raid just ended stops counting down -
    /// there is no map to return to - but nothing here moves the player anywhere; Debrief/PostRaid
    /// own that transition.
    /// </summary>
    public void SetInRaid(bool inRaid)
    {
        _inRaid = inRaid;
        RestartDeadline();
    }

    /// <summary>A new loot screenshot arrived while the page was already showing: restart the wait.</summary>
    public void NewLootScreenshot() => RestartDeadline();

    /// <summary>
    /// A screenshot arrived that was read as something other than a loot container - the player is
    /// moving again. Returns true when that means "go back to the map now".
    /// </summary>
    public bool NonLootScreenshot()
    {
        if (!IsActive)
        {
            return false;
        }

        _autoEntered = false;
        _deadlineUtc = null;
        return true;
    }

    /// <summary>Pins the page open ("Stay"), or releases the pin and restarts the countdown.</summary>
    public void Stay(bool stay)
    {
        _pinned = stay;
        RestartDeadline();
    }

    /// <summary>
    /// Call periodically (about once a second is plenty). Returns true exactly once, the moment
    /// the countdown reaches zero, meaning "go back to the map now".
    /// </summary>
    public bool Tick()
    {
        if (_deadlineUtc is not { } deadline || _clock.GetUtcNow() < deadline)
        {
            return false;
        }

        _autoEntered = false;
        _deadlineUtc = null;
        return true;
    }

    private void RestartDeadline() =>
        _deadlineUtc = IsActive && _timeout is { } timeout ? _clock.GetUtcNow() + timeout : null;
}
