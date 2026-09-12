using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.HttpResults;
using TarkovCompanion.GroupServer;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<GroupRooms>();

// The proxy terminates TLS and is the only thing that should be reaching this, so the real
// client address arrives in a header rather than on the connection.
builder.Services.Configure<Microsoft.AspNetCore.HttpOverrides.ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
        | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();
app.UseForwardedHeaders();

// The one secret the group shares. Set it in the environment; there is no default, because a
// default secret is no secret and this relays people's live positions.
var secret = app.Configuration["GROUP_SECRET"];
if (string.IsNullOrWhiteSpace(secret))
{
    app.Logger.LogCritical("GROUP_SECRET is not set. Refusing to start rather than run open.");
    return 1;
}

var rooms = app.Services.GetRequiredService<GroupRooms>();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// A member publishes themselves and is told about everyone else in one exchange, so there is
// no separate subscribe and no connection to hold open. A companion that is not running sends
// nothing and therefore shows nothing, which is the behaviour we want.
app.MapPost("/rooms/{room}/state", Results<Ok<GroupRoomState>, UnauthorizedHttpResult, BadRequest<string>> (
    string room,
    GroupMemberState state,
    HttpRequest request) =>
{
    if (!IsAuthorised(request, secret))
    {
        return TypedResults.Unauthorized();
    }

    if (string.IsNullOrWhiteSpace(room) || room.Length > 64)
    {
        return TypedResults.BadRequest("A room name is required and must be 64 characters or fewer.");
    }

    if (string.IsNullOrWhiteSpace(state.Name) || state.Name.Length > 48)
    {
        return TypedResults.BadRequest("A display name is required and must be 48 characters or fewer.");
    }

    // Keyed by the display name within the room, so a member who reconnects replaces their own
    // entry rather than appearing twice. Two people choosing the same name is their problem to
    // notice, and is better than a server that hands out identities.
    var key = state.Name;
    rooms.Publish(room, key, state);
    return TypedResults.Ok(rooms.Read(room, key));
});

app.MapDelete("/rooms/{room}/state/{name}", Results<Ok, UnauthorizedHttpResult> (
    string room,
    string name,
    HttpRequest request) =>
{
    if (!IsAuthorised(request, secret))
    {
        return TypedResults.Unauthorized();
    }

    rooms.Remove(room, name);
    return TypedResults.Ok();
});

app.Run();
return 0;

// Checks the shared secret without leaking how wrong a wrong one was. A fixed-time comparison,
// because an ordinary string comparison returns faster the earlier it finds a difference, and
// that is enough to recover a secret one character at a time.
static bool IsAuthorised(HttpRequest request, string secret)
{
    if (!request.Headers.TryGetValue("X-Group-Secret", out var provided) || provided.Count != 1)
    {
        return false;
    }

    var expected = Encoding.UTF8.GetBytes(secret);
    var actual = Encoding.UTF8.GetBytes(provided[0] ?? string.Empty);
    return CryptographicOperations.FixedTimeEquals(
        SHA256.HashData(expected),
        SHA256.HashData(actual));
}
