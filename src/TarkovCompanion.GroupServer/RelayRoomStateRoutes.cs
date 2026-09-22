using Microsoft.AspNetCore.Http.HttpResults;

namespace TarkovCompanion.GroupServer;

/// <summary>
/// The exchange every companion makes: publish yourself, be told about everybody else.
/// </summary>
/// <remarks>
/// Lifted out of Program.cs so a test can drive the route a relay actually serves rather than a
/// copy of it that agrees with it until it does not.
///
/// The addition here is a hold. A caller may say which revision of the room it already has and
/// how long it will wait, and the answer is kept back until the room moves on or that time
/// passes. A caller that says neither gets exactly what it always got, which is what lets an
/// older build keep working against a relay a newer one has already reached.
/// </remarks>
public static class RelayRoomStateRoutes
{
    /// <summary>How long a companion may say it will hold, before the relay's own bound.</summary>
    public const string WaitQuery = "wait";

    /// <summary>The revision the caller already has, from the answer it got last time.</summary>
    public const string SinceQuery = "since";

    public static RouteHandlerBuilder MapGroupRoomState(
        this IEndpointRouteBuilder app,
        GroupRooms rooms,
        GroupMarks marks,
        GroupRoomChanges changes)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(rooms);
        ArgumentNullException.ThrowIfNull(marks);
        ArgumentNullException.ThrowIfNull(changes);
        return app.MapPost(
            "/state",
            (GroupMemberState state, HttpRequest request, CancellationToken cancellationToken) =>
                PublishAndReadAsync(rooms, marks, changes, state, request, cancellationToken));
    }

    /// <summary>Publishes one member and answers with the room, holding it back where asked to.</summary>
    public static async Task<Results<Ok<GroupRoomState>, UnauthorizedHttpResult, BadRequest<string>>> PublishAndReadAsync(
        GroupRooms rooms,
        GroupMarks marks,
        GroupRoomChanges changes,
        GroupMemberState state,
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rooms);
        ArgumentNullException.ThrowIfNull(marks);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(request);
        if (!GroupKey.TryRead(request, out var key))
        {
            return TypedResults.Unauthorized();
        }

        // One validation, and it tolerates nulls. `"observed": null` used to reach
        // state.Observed.Count and throw, which the framework turned into a 500 — a malformed
        // request answered as a server fault, and an unhandled exception per attempt for anybody
        // who cared to send them.
        if (state is null)
        {
            return TypedResults.BadRequest("A member state is required.");
        }

        if (state.Validate() is { } invalid)
        {
            return TypedResults.BadRequest(invalid);
        }

        var room = GroupKey.RoomFor(key);
        // Keyed by the display name within the room, so a member who reconnects replaces their own
        // entry rather than appearing twice. Two people choosing the same name is their problem to
        // notice, and is better than a server that hands out identities.
        // Only a publish that changes what the room reads wakes the others' held exchanges (#453).
        if (rooms.Publish(room, state.Name, state))
        {
            changes.Record(room, state.Name);
        }

        var revision = changes.RevisionFor(room, state.Name);
        if (Wait(request) is { } wait && Since(request) is { } since)
        {
            // Held only for a caller that says both. One without the other is a caller that
            // cannot tell a change from the answer it already had, and holding for it would be
            // a wait with nothing at the end of it.
            revision = await changes
                .WaitAsync(room, state.Name, since, wait, cancellationToken)
                .ConfigureAwait(false);
        }

        var (waypoints, pings) = marks.Read(room);
        // The marks ride along on the exchange a client already makes every few seconds, so
        // nothing has to poll a second endpoint to find out the group moved a waypoint.
        return TypedResults.Ok(rooms.Read(room, state.Name) with
        {
            Waypoints = waypoints,
            Pings = pings,
            Revision = revision,
        });
    }

    private static TimeSpan? Wait(HttpRequest request) =>
        request.Query.TryGetValue(WaitQuery, out var values)
        && double.TryParse(values.ToString(), out var seconds)
        && double.IsFinite(seconds)
        && seconds > 0
            ? TimeSpan.FromSeconds(Math.Min(seconds, GroupRoomChanges.MaximumWait.TotalSeconds))
            : null;

    private static long? Since(HttpRequest request) =>
        request.Query.TryGetValue(SinceQuery, out var values)
        && long.TryParse(values.ToString(), out var since)
        && since >= 0
            ? since
            : null;
}
