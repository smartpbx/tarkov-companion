using TarkovCompanion.Application.Services.Planning;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;

namespace TarkovCompanion.Application.Services.Group;

/// <summary>[#712 T7] What this player's companion adds to the squad's ready check.</summary>
/// <param name="Check">Their own Loadout check for the planned map; null when it could not be made.</param>
/// <param name="Level">Their own level from the active profile; null when it is outside the game's range.</param>
public sealed record SharedReadiness(SharedLoadoutCheck? Check, int? Level)
{
    public static SharedReadiness None { get; } = new(null, null);
}

/// <summary>
/// [#712 T7] Makes this player's Loadout check and reads their level for the squad's ready check.
/// </summary>
/// <remarks>
/// On by default once in a squad (decision 5 on #712), with the "My ready check" switch on Team ›
/// Group as the way off. Only this player's own data: their active quests, their stash's owned
/// counts and their profile's level. Nothing is read about anybody who does not run the app.
///
/// Re-made at most every <see cref="RereadAfter"/> like the quest share, because the publish loop
/// runs every few seconds and a kit changes between raids; a new planned map re-makes it at once.
/// </remarks>
public sealed class GroupReadyCheckShare
{
    internal static readonly TimeSpan RereadAfter = TimeSpan.FromSeconds(30);

    private readonly IPlayerProfileService _profiles;
    private readonly Func<string?, CancellationToken, Task<LoadoutSuggestionPlan>> _plan;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SharedReadiness _shared = SharedReadiness.None;
    private DateTimeOffset _readUtc = DateTimeOffset.MinValue;
    private string? _mapId;

    public GroupReadyCheckShare(IPlayerProfileService profiles, LoadoutSuggestionService planner, TimeProvider? timeProvider = null)
        : this(profiles, (planner ?? throw new ArgumentNullException(nameof(planner))).PlanAsync, timeProvider)
    {
    }

    /// <summary>Over any planner, so the tests need no catalog.</summary>
    public GroupReadyCheckShare(
        IPlayerProfileService profiles,
        Func<string?, CancellationToken, Task<LoadoutSuggestionPlan>> plan,
        TimeProvider? timeProvider = null)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Raised when the check may have changed, so the group session sends now.</summary>
    public event Action? Changed;

    /// <summary>The map the check is made for, by quest catalog id; null lets the planner pick.</summary>
    public string? MapId => Volatile.Read(ref _mapId);

    /// <summary>Checks for another map: the one the squad is planning on Team.</summary>
    public void SetMap(string? mapId)
    {
        var next = string.IsNullOrWhiteSpace(mapId) ? null : mapId.Trim();
        if (string.Equals(Interlocked.Exchange(ref _mapId, next), next, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Invalidate();
    }

    /// <summary>Forgets the last check (a stash scan, a quest change), and says so.</summary>
    public void Invalidate()
    {
        _readUtc = DateTimeOffset.MinValue;
        Changed?.Invoke();
    }

    public async Task<SharedReadiness> GetAsync(CancellationToken cancellationToken)
    {
        if (WallClockAge.IsWithin(_time.GetUtcNow(), _readUtc, RereadAfter))
        {
            return _shared;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (WallClockAge.IsWithin(_time.GetUtcNow(), _readUtc, RereadAfter))
            {
                return _shared;
            }

            var profile = await _profiles.GetActiveAsync(cancellationToken).ConfigureAwait(false);
            var level = profile.Level is >= 1 and <= 79 ? profile.Level : (int?)null;
            var plan = await _plan(MapId, cancellationToken).ConfigureAwait(false);
            var check = plan.UnavailableReason is null && plan.MapId is not null
                ? SharedLoadoutCheck.From(plan, _time.GetUtcNow())
                : null;
            _shared = new(check, level);
            _readUtc = _time.GetUtcNow();
            return _shared;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A group exchange must not fail because the check could not be made; the last one stands.
            _readUtc = _time.GetUtcNow();
            return _shared;
        }
        finally
        {
            _gate.Release();
        }
    }
}
