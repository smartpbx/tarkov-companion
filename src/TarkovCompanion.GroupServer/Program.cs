using Microsoft.AspNetCore.Http.HttpResults;
using TarkovCompanion.GroupServer;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<GroupRooms>();

var app = builder.Build();
var rooms = app.Services.GetRequiredService<GroupRooms>();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

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

    var room = GroupKey.RoomFor(key);
    // Keyed by the display name within the room, so a member who reconnects replaces their own
    // entry rather than appearing twice. Two people choosing the same name is their problem to
    // notice, and is better than a server that hands out identities.
    rooms.Publish(room, state.Name, state);
    return TypedResults.Ok(rooms.Read(room, state.Name));
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
