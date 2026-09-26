using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Application.Services.Raids;

/// <summary>A raid the game just reported over, as the after-raid question needs it.</summary>
/// <param name="Squad">Who the game's group notifications said was in the party, by nickname; observed, never persisted.</param>
public sealed record RaidEnded(
    Guid RaidId,
    string? MapId,
    string? Side,
    DateTimeOffset? StartedUtc,
    DateTimeOffset EndedUtc,
    IReadOnlyList<string> Squad);

/// <summary>Says when a raid has just ended (#712 0-8).</summary>
/// <remarks>
/// Kept behind an interface so the after-raid card does not care where the edge comes from: today
/// the published runtime snapshot's raid state, later the Situation stream (#712 0-2).
/// </remarks>
public interface IRaidEndSignal
{
    event EventHandler<RaidEnded>? RaidEnded;
}

/// <summary>The in-raid to after-raid edge, detected from two consecutive raid snapshots.</summary>
public sealed class RaidEndDetector
{
    private RaidSnapshot? _inRaid;
    private IReadOnlyList<string> _squad = [];
    private readonly HashSet<Guid> _reported = [];

    /// <summary>
    /// Feeds the next snapshot; returns the raid that just ended, or null. Each raid is reported
    /// once, however many snapshots repeat its end.
    /// </summary>
    /// <remarks>
    /// Only a raid the companion saw in progress counts, and only when the game moved on to the
    /// post-raid screens or the menu. A raid that another raid replaced without an end line was
    /// never reported over (#568), so nothing is asked about it.
    /// </remarks>
    public RaidEnded? Observe(RaidSnapshot raid, SquadSnapshot squad)
    {
        ArgumentNullException.ThrowIfNull(raid);
        ArgumentNullException.ThrowIfNull(squad);
        if (raid.State == RaidLifecycleState.InRaid && raid.RaidId is not null)
        {
            _inRaid = raid;
            if (squad.HasMembers)
            {
                _squad = [.. squad.Members.Select(member => member.Nickname).OfType<string>().Where(name => name.Length > 0)];
            }

            return null;
        }

        if (_inRaid is not { RaidId: { } raidId } ended
            || raid.State is not (RaidLifecycleState.PostRaid or RaidLifecycleState.Menu))
        {
            return null;
        }

        _inRaid = null;
        var squadNames = _squad;
        _squad = [];
        if (raid.RaidId is { } current && current != raidId)
        {
            return null;
        }

        return _reported.Add(raidId)
            ? new RaidEnded(raidId, ended.MapId, ended.Side, ended.StartedUtc, raid.UpdatedUtc, squadNames)
            : null;
    }
}

/// <summary><see cref="IRaidEndSignal"/> over the published runtime snapshot.</summary>
public sealed class RuntimeRaidEndSignal : IRaidEndSignal, IDisposable
{
    private readonly IRuntimeStateStore _store;
    private readonly RaidEndDetector _detector = new();
    private readonly object _gate = new();

    public RuntimeRaidEndSignal(IRuntimeStateStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _store.Changed += OnChanged;
    }

    public event EventHandler<RaidEnded>? RaidEnded;

    public void Dispose() => _store.Changed -= OnChanged;

    private void OnChanged(object? sender, EventArgs e)
    {
        var snapshot = _store.Current;
        RaidEnded? ended;
        lock (_gate)
        {
            ended = _detector.Observe(snapshot.Raid, snapshot.Squad);
        }

        if (ended is not null)
        {
            RaidEnded?.Invoke(this, ended);
        }
    }
}
