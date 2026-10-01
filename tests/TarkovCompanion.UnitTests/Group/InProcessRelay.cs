using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using TarkovCompanion.GroupServer;

namespace TarkovCompanion.UnitTests.Group;

/// <summary>
/// A group relay in this process: the relay's own /state route and the /pings, /waypoints and
/// DELETE handlers Program.cs maps, on a loopback port. Records every mark it is sent.
/// </summary>
internal sealed class InProcessRelay(WebApplication app, string address, ConcurrentQueue<string> received) : IAsyncDisposable
{
    public string Address { get; } = address;

    /// <summary>"ping customs by Alpha", one line per mark the relay accepted, oldest first.</summary>
    public IReadOnlyList<string> Marks => [.. received];

    /// <param name="port">A port to listen on, so a client can be pointed at a relay that is not up yet; 0 picks one.</param>
    public static async Task<InProcessRelay> StartAsync(int port = 0)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Logging.ClearProviders();
        var rooms = new GroupRooms(TimeProvider.System);
        var changes = new GroupRoomChanges(TimeProvider.System);
        var marks = new GroupMarks(TimeProvider.System);
        var app = builder.Build();
        var received = new ConcurrentQueue<string>();
        app.MapGroupRoomState(rooms, marks, changes);
        app.MapPost("/pings", (MarkRequest request, HttpRequest http) =>
        {
            if (!GroupKey.TryRead(http, out var key) || request.Validate() is not null)
            {
                return Results.BadRequest();
            }

            var room = GroupKey.RoomFor(key);
            var added = marks.AddPing(room, request.By, request.MapId, request.X, request.Y, request.Z, request.Label)!;
            changes.Record(room, null);
            received.Enqueue($"ping {request.MapId} by {request.By}");
            return Results.Ok(added);
        });
        app.MapPost("/waypoints", (MarkRequest request, HttpRequest http) =>
        {
            if (!GroupKey.TryRead(http, out var key) || request.Validate() is not null)
            {
                return Results.BadRequest();
            }

            var room = GroupKey.RoomFor(key);
            var added = marks.AddWaypoint(room, request.By, request.MapId, request.X, request.Y, request.Z, request.Label)!;
            changes.Record(room, null);
            received.Enqueue($"waypoint {request.MapId} by {request.By}");
            return Results.Ok(added);
        });
        app.MapDelete("/waypoints/{id:long}", (long id, string? by, HttpRequest http) =>
        {
            if (!GroupKey.TryRead(http, out var key))
            {
                return Results.Unauthorized();
            }

            var room = GroupKey.RoomFor(key);
            if (!marks.Remove(room, id, by))
            {
                return Results.NotFound();
            }

            changes.Record(room, null);
            return Results.Ok();
        });
        await app.StartAsync();
        return new InProcessRelay(app, app.Urls.First() + "/", received);
    }

    /// <summary>A loopback port nothing is listening on, for a relay that starts later.</summary>
    public static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }
}
