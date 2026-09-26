using System.Text.Json.Serialization;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.GroupServer.Tenancy;

namespace TarkovCompanion.GroupServer;

/// <summary>[#920] Whether this desktop's session is the relay owner's.</summary>
public sealed record RelayAdminStatus([property: JsonPropertyName("relayOwner")] bool RelayOwner);

/// <summary>[#920] One live member of a room, as the relay owner sees it.</summary>
public sealed record RelayAdminMember(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("lastSeenUtc")] DateTimeOffset LastSeenUtc,
    [property: JsonPropertyName("drawings")] int Drawings);

/// <summary>[#920] One room: who is in it, when it last moved, and what it holds.</summary>
public sealed record RelayAdminRoom(
    [property: JsonPropertyName("room")] string Room,
    [property: JsonPropertyName("label")] string? Label,
    [property: JsonPropertyName("lastActivityUtc")] DateTimeOffset? LastActivityUtc,
    [property: JsonPropertyName("waypoints")] int Waypoints,
    [property: JsonPropertyName("pings")] int Pings,
    [property: JsonPropertyName("members")] IReadOnlyList<RelayAdminMember> Members,
    [property: JsonPropertyName("removed")] IReadOnlyList<string> Removed);

public sealed record RelayAdminRooms(
    [property: JsonPropertyName("serverUtc")] DateTimeOffset ServerUtc,
    [property: JsonPropertyName("rooms")] IReadOnlyList<RelayAdminRoom> Rooms);

/// <summary>[#920] What one admin action did: how many things it took away.</summary>
public sealed record RelayAdminOutcome([property: JsonPropertyName("cleared")] int Cleared);

/// <summary>Who an admin request was accepted from, for the log.</summary>
public enum RelayAdminAuthority
{
    None,
    AdminKey,
    OwnerSession,
}

/// <summary>
/// [#920] The relay owner's controls over the rooms: clear lines, clear marks, remove a member,
/// reset a room, and the list that says which rooms there are.
/// </summary>
/// <remarks>
/// Who counts as the owner is the question that matters here. Every desktop registered since #553
/// owns its own tenant — its tablets — and a squadmate's desktop is exactly that, so an owner
/// session alone is not enough. Two things are: the operator's admin key, or a live owner session
/// of the <b>legacy</b> tenant, which only the admin-key claim (<c>/admin/relay/claim</c>) ever
/// creates and which the desktop that made it keeps by key possession. A plain group key opens
/// none of this.
///
/// Every route is under <c>/admin/</c>, so the wrong-key limiter counts a refused one the same as
/// any other admin route (<see cref="RelayAccess.IsAdminPath"/>). Each action is idempotent — a
/// second clear clears nothing and says 0 — ends held exchanges so squadmates see it on their next
/// one, and is logged with the room's first eight characters and never a key or credential.
/// </remarks>
public static class RelayOwnerAdminRoutes
{
    public const string StatusPath = "/v2/companion/relay/admin-status";

    public static void MapRelayOwnerAdmin(
        this WebApplication app,
        RelayTenantDirectory? directory,
        GroupRooms rooms,
        GroupMarks marks,
        GroupRoomChanges changes,
        GroupRoomModeration moderation,
        GroupRoomRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(rooms);
        ArgumentNullException.ThrowIfNull(marks);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(moderation);
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("RelayOwnerAdmin");
        var clock = app.Services.GetService<TimeProvider>() ?? TimeProvider.System;

        // Whether to show the panel at all. Any live desktop session may ask; only the relay
        // owner's is told yes. Not under /admin/ on purpose: a squadmate's desktop asking once is
        // not a wrong key, and must not be counted as one.
        app.MapGet(StatusPath, async Task<IResult> (HttpRequest request, CancellationToken cancellationToken) =>
        {
            if (directory is null ||
                await RelayCompanionRoutes.AuthenticateAsync(request, directory, cancellationToken).ConfigureAwait(false) is not var (tenant, principal))
            {
                return Results.Unauthorized();
            }

            return Results.Ok(new RelayAdminStatus(tenant.IsLegacy && principal.Role == DeviceAuthorizationRole.Owner));
        });

        app.MapGet("/admin/owner/rooms", async Task<IResult> (HttpRequest request, CancellationToken cancellationToken) =>
        {
            if (await AuthoriseAsync(request, directory, cancellationToken).ConfigureAwait(false) == RelayAdminAuthority.None)
            {
                return Results.Unauthorized();
            }

            var snapshot = rooms.AdminSnapshot();
            var labels = registry?.List().ToDictionary(room => room.Room, room => room.Label, StringComparer.Ordinal)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
            var names = snapshot.Keys.Union(labels.Keys, StringComparer.Ordinal).ToArray();
            var list = names
                .Select(room =>
                {
                    var members = snapshot.GetValueOrDefault(room) ?? [];
                    var (waypoints, pings) = marks.Read(room);
                    var last = members.Select(member => (DateTimeOffset?)member.LastSeenUtc)
                        .Concat(waypoints.Select(waypoint => (DateTimeOffset?)waypoint.CreatedUtc))
                        .Concat(pings.Select(ping => (DateTimeOffset?)ping.CreatedUtc))
                        .Max();
                    return new RelayAdminRoom(
                        room,
                        labels.GetValueOrDefault(room),
                        last,
                        waypoints.Count,
                        pings.Count,
                        [.. members.Select(member => new RelayAdminMember(member.Name, member.LastSeenUtc, member.Drawings))],
                        moderation.RemovedFrom(room));
                })
                .OrderByDescending(room => room.Members.Count)
                .ThenByDescending(room => room.LastActivityUtc)
                .Take(64)
                .ToArray();
            return Results.Ok(new RelayAdminRooms(clock.GetUtcNow(), list));
        });

        app.MapPost("/admin/owner/rooms/{room}/drawings/clear", async Task<IResult> (
            string room,
            string? member,
            HttpRequest request,
            CancellationToken cancellationToken) =>
        {
            var authority = await AuthoriseAsync(request, directory, cancellationToken).ConfigureAwait(false);
            if (authority == RelayAdminAuthority.None)
            {
                return Results.Unauthorized();
            }

            if (!IsRoom(room) || (member is not null && !IsMember(member)))
            {
                return Results.BadRequest("room-or-member-invalid");
            }

            var cleared = ClearDrawings(rooms, moderation, room, member);
            changes.Record(room, null);
            Log(log, "clear-drawings", room, authority, cleared, member is not null);
            return Results.Ok(new RelayAdminOutcome(cleared));
        });

        app.MapPost("/admin/owner/rooms/{room}/marks/clear", async Task<IResult> (
            string room,
            HttpRequest request,
            CancellationToken cancellationToken) =>
        {
            var authority = await AuthoriseAsync(request, directory, cancellationToken).ConfigureAwait(false);
            if (authority == RelayAdminAuthority.None)
            {
                return Results.Unauthorized();
            }

            if (!IsRoom(room))
            {
                return Results.BadRequest("room-or-member-invalid");
            }

            var cleared = marks.ClearAll(room);
            changes.Record(room, null);
            Log(log, "clear-marks", room, authority, cleared, member: false);
            return Results.Ok(new RelayAdminOutcome(cleared));
        });

        app.MapPost("/admin/owner/rooms/{room}/members/remove", async Task<IResult> (
            string room,
            string? member,
            HttpRequest request,
            CancellationToken cancellationToken) =>
        {
            var authority = await AuthoriseAsync(request, directory, cancellationToken).ConfigureAwait(false);
            if (authority == RelayAdminAuthority.None)
            {
                return Results.Unauthorized();
            }

            if (!IsRoom(room) || member is null || !IsMember(member))
            {
                return Results.BadRequest("room-or-member-invalid");
            }

            // Removed first, so a publish landing between the two cannot put them straight back.
            if (!moderation.Remove(room, member))
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            var present = rooms.Holds(room, member);
            rooms.Remove(room, member);
            changes.Record(room, null);
            Log(log, "remove-member", room, authority, present ? 1 : 0, member: true);
            return Results.Ok(new RelayAdminOutcome(present ? 1 : 0));
        });

        // Everything the room holds, gone: marks, lines, members, and any removals. Members are
        // forgotten rather than removed, so they are back on their next exchange — without the
        // lines they had drawn, which stay cleared.
        app.MapPost("/admin/owner/rooms/{room}/reset", async Task<IResult> (
            string room,
            HttpRequest request,
            CancellationToken cancellationToken) =>
        {
            var authority = await AuthoriseAsync(request, directory, cancellationToken).ConfigureAwait(false);
            if (authority == RelayAdminAuthority.None)
            {
                return Results.Unauthorized();
            }

            if (!IsRoom(room))
            {
                return Results.BadRequest("room-or-member-invalid");
            }

            moderation.Forget(room);
            var cleared = ClearDrawings(rooms, moderation, room, member: null) + marks.ClearAll(room);
            var members = rooms.AdminSnapshot().GetValueOrDefault(room)?.Count ?? 0;
            rooms.Clear(room);
            changes.Record(room, null);
            Log(log, "reset-room", room, authority, cleared + members, member: false);
            return Results.Ok(new RelayAdminOutcome(cleared + members));
        });
    }

    /// <summary>The admin key, or a live owner session of the legacy tenant; see the class remarks.</summary>
    public static async ValueTask<RelayAdminAuthority> AuthoriseAsync(
        HttpRequest request,
        RelayTenantDirectory? directory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (RelayAdmin.IsAuthorised(request))
        {
            return RelayAdminAuthority.AdminKey;
        }

        if (directory is null ||
            await RelayCompanionRoutes.AuthenticateAsync(request, directory, cancellationToken).ConfigureAwait(false) is not var (tenant, principal))
        {
            return RelayAdminAuthority.None;
        }

        return tenant.IsLegacy && principal.Role == DeviceAuthorizationRole.Owner
            ? RelayAdminAuthority.OwnerSession
            : RelayAdminAuthority.None;
    }

    private static int ClearDrawings(GroupRooms rooms, GroupRoomModeration moderation, string room, string? member)
    {
        var taken = rooms.TakeDrawings(room, member);
        foreach (var (name, ids) in taken)
        {
            moderation.Clear(room, name, ids);
        }

        return taken.Values.Sum(ids => ids.Count);
    }

    private static bool IsRoom(string room) =>
        room.Length == 32 && room.All(character => char.IsAsciiHexDigitLower(character) || char.IsAsciiDigit(character));

    private static bool IsMember(string member) =>
        !string.IsNullOrWhiteSpace(member) && member.Length <= 64;

    private static void Log(ILogger log, string action, string room, RelayAdminAuthority authority, int count, bool member) =>
        log.LogInformation(
            "Relay admin {Action} in room {Room}… by {Authority}{Scope}: {Count}",
            action,
            room[..8],
            authority,
            member ? " (one member)" : string.Empty,
            count);
}
