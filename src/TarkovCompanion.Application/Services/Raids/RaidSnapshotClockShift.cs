using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Application.Services.Raids;

/// <summary>
/// Moves every wall time a raid snapshot holds by a clock jump (#799).
/// </summary>
/// <remarks>
/// A screenshot's time comes from its filename and a raid's from the game's log, and the game
/// writes both in the PC's local time. Once the PC's clock is set, the next screenshot is in the
/// new frame and every stamp already held is in the old one: after a four-hour correction
/// backwards, each new position was "older" than the last and refused, so the player's own
/// marker stopped moving. Moving the held stamps by the same jump puts them back in one frame.
///
/// Nothing persisted is touched. The raid's row keeps the start it was recorded with, because
/// that is what the PC said at the time and rewriting history to agree with a later clock would
/// be a second, silent correction.
/// </remarks>
public static class RaidSnapshotClockShift
{
    public static RaidSnapshot Shift(RaidSnapshot snapshot, TimeSpan jump)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (jump == TimeSpan.Zero)
        {
            return snapshot;
        }

        return snapshot with
        {
            StartedUtc = Shift(snapshot.StartedUtc, jump),
            // The epoch is the "never updated" sentinel of an empty snapshot, not a time.
            UpdatedUtc = snapshot.UpdatedUtc == DateTimeOffset.UnixEpoch ? snapshot.UpdatedUtc : snapshot.UpdatedUtc + jump,
            LastActivityUtc = Shift(snapshot.LastActivityUtc, jump),
            RaidClockReadUtc = Shift(snapshot.RaidClockReadUtc, jump),
            LastKnownPosition = Shift(snapshot.LastKnownPosition, jump),
            PositionTrail = [.. snapshot.PositionTrail.Select(step => Shift(step, jump)!)],
            Hud = snapshot.Hud is { } hud ? hud with { ReadUtc = hud.ReadUtc + jump } : null,
        };
    }

    private static DateTimeOffset? Shift(DateTimeOffset? value, TimeSpan jump) =>
        value is { } stamp ? stamp + jump : null;

    private static ScreenshotPosition? Shift(ScreenshotPosition? position, TimeSpan jump) =>
        position is null ? null : position with { Timestamp = position.Timestamp + jump };
}
