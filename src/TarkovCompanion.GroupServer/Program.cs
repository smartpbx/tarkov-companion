using Microsoft.AspNetCore.Http.HttpResults;
using TarkovCompanion.GroupServer;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<GroupRooms>();
builder.Services.AddSingleton<GroupMarks>();
// One copy of the game-data catalog for the whole group, instead of five clients each pulling
// several megabytes of the same answer. Its own client, with its own timeout, because a slow
// upstream must not hold up the group exchange this server mainly exists for.
builder.Services.AddHttpClient<CatalogMirror>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("TarkovCompanion-GroupServer/1.0");
});

var app = builder.Build();
var rooms = app.Services.GetRequiredService<GroupRooms>();
var marks = app.Services.GetRequiredService<GroupMarks>();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// The second screen.
//
// Alt-tabbing out of a raid to drop a waypoint is the thing that makes a companion not worth
// using, and a tablet cannot run the desktop application at all, so the choice here is a web
// surface or nothing. Served from this server because it is already running, already reachable
// by everybody in the group, and already holds the state the page shows.
//
// One file, embedded, no framework and no build step. It is read-only toward the group state
// and writes only marks, which is the same thing the desktop client's right-click does.
//
// It is not a replacement for the desktop application and must never become one: the desktop
// client stays complete on its own, and somebody playing alone needs none of this.
app.MapGet("/", () => Results.Content(Tablet.Page, "text/html; charset=utf-8"));

app.MapGet("/tablet", () => Results.Content(Tablet.Page, "text/html; charset=utf-8"));

// A member publishes themselves and is told about everyone else in one exchange, so there is
// no separate subscribe and no connection to hold open. A companion that is not running sends
// nothing and therefore shows nothing, which is the behaviour we want.
//
// The room is not in the URL any more. It is derived from the group's key, which is the only
// thing a member configures: see GroupKey for why one value replaced two.
app.MapPost("/state", Results<Ok<GroupRoomState>, UnauthorizedHttpResult, BadRequest<string>> (
    GroupMemberState state,
    HttpRequest request) =>
{
    if (!TryReadKey(request, out var key))
    {
        return TypedResults.Unauthorized();
    }

    if (string.IsNullOrWhiteSpace(state.Name) || state.Name.Length > 48)
    {
        return TypedResults.BadRequest("A display name is required and must be 48 characters or fewer.");
    }

    // Bounded because this is the one field carrying something about other people, and a
    // client that published four hundred of them would be filling the room rather than
    // helping it. A party is five.
    if (state.Observed.Count > 8 || state.Observed.Any(observed =>
        string.IsNullOrWhiteSpace(observed.Name) || observed.Name.Length > 48 || observed.Loadout.Count > 12))
    {
        return TypedResults.BadRequest("Observations must name at most eight players with at most twelve items each.");
    }

    var room = GroupKey.RoomFor(key);
    // Keyed by the display name within the room, so a member who reconnects replaces their own
    // entry rather than appearing twice. Two people choosing the same name is their problem to
    // notice, and is better than a server that hands out identities.
    rooms.Publish(room, state.Name, state);
    var (waypoints, pings) = marks.Read(room);
    // The marks ride along on the exchange a client already makes every few seconds, so
    // nothing has to poll a second endpoint to find out the group moved a waypoint.
    return TypedResults.Ok(rooms.Read(room, state.Name) with
    {
        Waypoints = waypoints,
        Pings = pings,
    });
});

// Reading the room without joining it.
//
// The exchange above is the companion's: it publishes and is answered in one round trip, which
// is right for something that has a position to contribute. A second screen has nothing to
// contribute -- it is not in the raid -- and joining as a member would put a phantom marker in
// the group and a phantom name in everybody's panel.
//
// So this returns everyone, including whoever is reading, because the reader is not one of
// them. Same key, because this is the same room and the key is the whole access model.
app.MapGet("/state", Results<Ok<GroupRoomState>, UnauthorizedHttpResult> (HttpRequest request) =>
{
    if (!TryReadKey(request, out var key))
    {
        return TypedResults.Unauthorized();
    }

    var room = GroupKey.RoomFor(key);
    var (waypoints, pings) = marks.Read(room);
    return TypedResults.Ok(rooms.Read(room, exceptMemberKey: string.Empty) with
    {
        Waypoints = waypoints,
        Pings = pings,
    });
});

// Marks. A waypoint is a plan and stays; a ping is "look here" and fades. Both belong to the
// group rather than to whoever dropped them, which is the first thing on this server that one
// member says TO the others rather than about themselves.
app.MapPost("/waypoints", Results<Ok<GroupWaypoint>, UnauthorizedHttpResult, BadRequest<string>> (
    MarkRequest request,
    HttpRequest http) =>
{
    if (!TryReadKey(http, out var key))
    {
        return TypedResults.Unauthorized();
    }

    if (request.Validate() is { } problem)
    {
        return TypedResults.BadRequest(problem);
    }

    return TypedResults.Ok(marks.AddWaypoint(
        GroupKey.RoomFor(key), request.By, request.MapId, request.X, request.Y, request.Z, request.Label));
});

app.MapPost("/pings", Results<Ok<GroupPing>, UnauthorizedHttpResult, BadRequest<string>> (
    MarkRequest request,
    HttpRequest http) =>
{
    if (!TryReadKey(http, out var key))
    {
        return TypedResults.Unauthorized();
    }

    if (request.Validate() is { } problem)
    {
        return TypedResults.BadRequest(problem);
    }

    return TypedResults.Ok(marks.AddPing(
        GroupKey.RoomFor(key), request.By, request.MapId, request.X, request.Y, request.Z, request.Label));
});

app.MapPost("/waypoints/{id:long}/reached", Results<Ok, NotFound, UnauthorizedHttpResult, BadRequest<string>> (
    long id,
    ReachedRequest request,
    HttpRequest http) =>
{
    if (!TryReadKey(http, out var key))
    {
        return TypedResults.Unauthorized();
    }

    if (string.IsNullOrWhiteSpace(request.By) || request.By.Length > 48)
    {
        return TypedResults.BadRequest("A display name is required and must be 48 characters or fewer.");
    }

    return marks.Complete(GroupKey.RoomFor(key), id, request.By)
        ? TypedResults.Ok()
        : TypedResults.NotFound();
});

app.MapDelete("/waypoints/{id:long}", Results<Ok, NotFound, UnauthorizedHttpResult> (
    long id,
    HttpRequest http) =>
{
    if (!TryReadKey(http, out var key))
    {
        return TypedResults.Unauthorized();
    }

    return marks.Remove(GroupKey.RoomFor(key), id) ? TypedResults.Ok() : TypedResults.NotFound();
});

// Anyone in the group may clear the group's marks, because they are the group's. A server that
// tracked who owned what would need identities, and this one deliberately has none.
app.MapDelete("/waypoints", Results<Ok<int>, UnauthorizedHttpResult> (
    string? mapId,
    bool? reachedOnly,
    HttpRequest http) =>
{
    if (!TryReadKey(http, out var key))
    {
        return TypedResults.Unauthorized();
    }

    return TypedResults.Ok(marks.Clear(GroupKey.RoomFor(key), mapId, reachedOnly == true));
});

app.MapDelete("/state/{name}", Results<Ok, UnauthorizedHttpResult> (
    string name,
    HttpRequest request) =>
{
    if (!TryReadKey(request, out var key))
    {
        return TypedResults.Unauthorized();
    }

    rooms.Remove(GroupKey.RoomFor(key), name);
    return TypedResults.Ok();
});

// The game-data catalog, served once for the group.
//
// Deliberately outside the group key. The catalog is public data that anybody can fetch from
// json.tarkov.dev without asking anybody, so putting it behind the key would protect nothing
// and would stop a client that has not been configured for a group from using the mirror at
// all. What it is not is an open proxy: the path is checked against a list, and nothing else
// is ever fetched.
app.MapGet("/catalog", (CatalogMirror mirror) => TypedResults.Ok(mirror.Index()));

app.MapGet("/catalog/{mode}/{endpoint}", async Task<IResult> (
    string mode,
    string endpoint,
    CatalogMirror mirror,
    HttpRequest request,
    CancellationToken cancellationToken) =>
{
    if (!CatalogMirror.IsAllowed(mode, endpoint))
    {
        return TypedResults.NotFound();
    }

    var snapshot = await mirror.GetAsync(mode, endpoint, cancellationToken);
    if (snapshot is null)
    {
        // Nothing held and upstream unreachable. 503 rather than an error body, so the client
        // falls back to upstream instead of caching a failure under a strong tag.
        return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    // The whole point of a content-addressed snapshot: a client holding this tag is holding
    // these bytes, and gets no body at all.
    if (request.Headers.IfNoneMatch.Any(value => string.Equals(value, snapshot.ETag, StringComparison.Ordinal)))
    {
        return TypedResults.StatusCode(StatusCodes.Status304NotModified);
    }

    request.HttpContext.Response.Headers.ETag = snapshot.ETag;
    return TypedResults.Bytes(snapshot.Body, "application/json");
});

app.Run();

// The only thing checked is that a key is long enough to be a key. There is nothing to compare
// it against, because the server holds no secrets: a key that nobody else uses simply names a
// room that nobody else is in. Refusing a short one is not access control, it is stopping
// somebody from believing "a" protects their group.
static bool TryReadKey(HttpRequest request, out string key)
{
    key = string.Empty;
    if (!request.Headers.TryGetValue("X-Group-Key", out var provided) || provided.Count != 1)
    {
        return false;
    }

    var candidate = provided[0];
    if (!GroupKey.IsAcceptable(candidate))
    {
        return false;
    }

    key = candidate!;
    return true;
}

/// <summary>A place somebody is marking, from whoever is marking it.</summary>
public sealed record MarkRequest(string By, string MapId, double X, double Y, double Z, string? Label)
{
    public string? Validate() =>
        string.IsNullOrWhiteSpace(By) || By.Length > 48
            ? "A display name is required and must be 48 characters or fewer."
            : string.IsNullOrWhiteSpace(MapId) || MapId.Length > 64
                ? "A map is required and must be 64 characters or fewer."
                : !double.IsFinite(X) || !double.IsFinite(Y) || !double.IsFinite(Z)
                    ? "The position must be finite."
                    : null;
}

public sealed record ReachedRequest(string By);
