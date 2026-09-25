using System.Globalization;
using System.Text;
using TarkovCompanion.Application.Services.Group;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

/// <summary>
/// [#893] What the Raid map draws from a <see cref="GroupSnapshot"/>, as one comparable string.
/// </summary>
/// <remarks>
/// The group session publishes a new snapshot on every relay exchange, about three a second with a
/// squad, and the cockpit used to rebuild its whole scene whenever the reference changed. On the
/// owner's logs that was 195 whole-scene rebuilds a minute (the "[loot-layer] ... reused in 60 s"
/// breadcrumb), almost all of them identical to the one before.
///
/// Everything a reader of the group on the Raid map can show is in here: positions, headings,
/// trails, marks, drawings, raid state, objectives, the relay's stale flag. The clocks that tick on
/// every exchange are not: <see cref="GroupSnapshot.UpdatedUtc"/>, <see cref="GroupMemberView.Since"/>,
/// the raw position and trail ages, the latency figures and the status line. Ages go in as what the
/// map shows of them (the marker's age text, whether it is faded, whether the member has gone
/// quiet), so the "from a screenshot 30 s ago" text still moves on when its words would change.
///
/// A moved squadmate always changes the signature, so a new position is never held back by this.
/// </remarks>
internal static class RaidGroupSceneSignature
{
    public static string Of(GroupSnapshot group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var text = new StringBuilder(256);
        text.Append(group.IsSharing ? 'S' : '-')
            .Append(group.StaleSince is null ? 'L' : 'Q')
            .Append('|').Append(group.Room)
            .Append('|').Append(group.MyGameMode)
            .Append('|').Append(group.MySide)
            .Append('|').Append(group.MyLevel);
        Join(text, group.MyLoadout);

        foreach (var member in group.Members)
        {
            text.Append("\nM|").Append(member.Name)
                .Append('|').Append(member.MapId)
                .Append('|').Append((int)member.RaidState)
                .Append('|').Append(member.Side)
                .Append('|').Append(member.GameMode)
                .Append('|').Append(member.HasKnownHeight ? 'h' : '-')
                .Append(member.HasGoneQuiet ? 'q' : '-');
            if (member.Position is { } position)
            {
                Point(text, position.X, position.Y, position.Z);
            }

            Number(text, member.HeadingDegrees);
            var age = member.PositionAgeNow ?? TimeSpan.MaxValue;
            text.Append('|').Append(RaidCockpitViewModel.Describe(age))
                .Append(age > RaidCockpitViewModel.PositionFreshFor ? 'o' : 'f');
            Number(text, member.RaidClock?.TotalSeconds);
            text.Append('|').Append(member.Ready).Append('|').Append(member.PlannedExtract).Append('|').Append(member.Note);
            Join(text, member.Loadout);
            Join(text, member.Quests);
            Join(text, member.QuestIds);
            Join(text, member.Extracts);
            Join(text, member.Transits);
            foreach (var objective in member.Objectives)
            {
                text.Append("|o:").Append(objective.TaskId).Append('/').Append(objective.ObjectiveId).Append('=').Append(objective.Count);
            }

            text.Append("|t");
            foreach (var step in member.Trail)
            {
                Point(text, step.X, step.Y ?? 0, step.Z);
            }

            foreach (var drawing in member.Drawings)
            {
                text.Append("|d:").Append(drawing.Id).Append('/').Append(drawing.MapId).Append('/').Append(drawing.FloorId);
                foreach (var (x, z) in drawing.Points)
                {
                    Point(text, x, 0, z);
                }
            }
        }

        foreach (var waypoint in group.Waypoints)
        {
            text.Append("\nW|").Append(waypoint.Id).Append('|').Append(waypoint.By).Append('|').Append(waypoint.MapId)
                .Append('|').Append(waypoint.Label).Append('|').Append(waypoint.Reached).Append('|').Append(waypoint.Colour);
            Point(text, waypoint.X, waypoint.Y, waypoint.Z);
        }

        foreach (var ping in group.Pings)
        {
            text.Append("\nP|").Append(ping.Id).Append('|').Append(ping.By).Append('|').Append(ping.MapId)
                .Append('|').Append(ping.Label).Append('|').Append(ping.Colour);
            Point(text, ping.X, ping.Y, ping.Z);
        }

        return text.ToString();
    }

    // Ten centimetres: far below anything the plan can draw apart, and above float noise.
    private static void Point(StringBuilder text, double x, double y, double z) => text
        .Append('(').Append(Math.Round(x, 1).ToString("R", CultureInfo.InvariantCulture))
        .Append(',').Append(Math.Round(y, 1).ToString("R", CultureInfo.InvariantCulture))
        .Append(',').Append(Math.Round(z, 1).ToString("R", CultureInfo.InvariantCulture)).Append(')');

    private static void Number(StringBuilder text, double? value)
    {
        text.Append('|');
        if (value is { } number)
        {
            text.Append(Math.Round(number, 1).ToString("R", CultureInfo.InvariantCulture));
        }
    }

    private static void Join(StringBuilder text, IReadOnlyList<string> values)
    {
        text.Append('[');
        foreach (var value in values)
        {
            text.Append(value).Append('\u001f');
        }

        text.Append(']');
    }
}
