using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Reflection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.GroupServer;
using TarkovCompanion.GroupServer.Diagnostics;
using TarkovCompanion.GroupServer.Security;
using TarkovCompanion.GroupServer.StateSync;
using TarkovCompanion.GroupServer.Storage;
using TarkovCompanion.GroupServer.Tenancy;

var builder = WebApplication.CreateBuilder(args);

// Kestrel's default is 30 MB. Nothing this relay accepts is larger than a few kilobytes, and a
// 1 GB box should not be asked to buffer thirty megabytes because somebody sent it.
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 32 * 1024);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<GroupRooms>();
// v2r-fast-positions (package 31): what has changed in each room, so POST /state can hold its
// answer until there is something new in it instead of making a caller wait for its own tick.
builder.Services.AddSingleton<GroupRoomChanges>();
// StateDirectory=tarkov-group gives the unit /var/lib/tarkov-group, which is outside the tree
// the updater replaces with `rm -rf /opt/tarkov-group` — so a plan survives the update that
// used to destroy it. Falls back to memory-only where the directory is not configured, which
// is what every test and every local run gets.
builder.Services.AddSingleton(provider => new GroupMarks(
    provider.GetRequiredService<TimeProvider>(),
    MarksStorePath()));
// The rooms an operator meant to exist. Beside the marks and for the same reason: the relay
// replaces its own tree every half hour, and an allowlist that did not survive that would lock
// the group out of their own rooms on a schedule.
builder.Services.AddSingleton(provider => new GroupRoomRegistry(
    provider.GetRequiredService<TimeProvider>(),
    StorePath("rooms.json")));
// Which build is on the box against which build the signed release ring selects, and the file
// that asks for the difference. Read from the updater's own stamps: only the updater holds the
// feed credential and the trust root, so the panel makes no release request of its own.
builder.Services.AddSingleton(_ => new RelayUpdate(StateDirectory(), UpdateStatusDirectory()));

// Where the squad's marks are kept, or null to hold them in memory as before.
//
// STATE_DIRECTORY is set by systemd when the unit declares StateDirectory=tarkov-group, which
// resolves to /var/lib/tarkov-group — outside the tree the updater replaces with
// `rm -rf /opt/tarkov-group`, which is the whole point. TARKOV_GROUP_STATE overrides it for a
// deployment that is not systemd, and absent both this stays memory-only, which is what every
// test and every local run gets.
static string? MarksStorePath() => StorePath("marks.json");

/// <summary>Where this deployment keeps state, or null when it keeps none.</summary>
static string? StateDirectory()
{
    if (Environment.GetEnvironmentVariable("TARKOV_GROUP_STATE") is { Length: > 0 } explicitPath)
    {
        return explicitPath;
    }

    return Environment.GetEnvironmentVariable("STATE_DIRECTORY") is { Length: > 0 } stateDirectory
        ? stateDirectory.Split(':')[0]
        : null;
}

/// <summary>Where the root updater publishes what the panel shows, or null where nothing does.</summary>
/// <remarks>
/// Root's directory, which this process reads and cannot write, beside rather than inside its own
/// state directory: a status this relay could write would be a status a compromised relay could
/// forge. TARKOV_RELAY_UPDATE_STATUS overrides the path the updater uses by default.
/// </remarks>
static string? UpdateStatusDirectory()
{
    if (Environment.GetEnvironmentVariable("TARKOV_RELAY_UPDATE_STATUS") is { Length: > 0 } explicitPath)
    {
        return explicitPath;
    }

    return StateDirectory() is null ? null : "/var/lib/tarkov-group-update-status";
}

/// <summary>One file in whatever directory this deployment keeps state in, or null for none.</summary>
static string? StorePath(string fileName) =>
    StateDirectory() is { } directory ? Path.Combine(directory, fileName) : null;

/// <summary>
/// The protected operator secret <see cref="OwnerRecoveryProtector"/> signs a claim grant with, or
/// null when unset. Standard base64 (not base64url, so an operator can generate one with
/// <c>openssl rand -base64 32</c> unmodified), at least 32 decoded bytes; anything else is treated
/// as unset rather than accepted short, the same fail-closed rule <see cref="RelayAdmin"/> follows.
/// </summary>
static byte[]? OwnerRecoverySecret()
{
    var configured = Environment.GetEnvironmentVariable("TARKOV_RELAY_OWNER_RECOVERY_SECRET");
    if (string.IsNullOrWhiteSpace(configured))
    {
        return null;
    }

    try
    {
        var decoded = Convert.FromBase64String(configured);
        return decoded.Length >= 32 ? decoded : null;
    }
    catch (FormatException)
    {
        return null;
    }
}
// One copy of the game-data catalog for the whole group, instead of five clients each pulling
// several megabytes of the same answer. Its own client, with its own timeout, because a slow
// upstream must not hold up the group exchange this server mainly exists for.
//
// A singleton, and that is the whole point of it. Registered as a typed client with
// AddHttpClient<CatalogMirror> it was transient: every request built a new mirror with an empty
// store and its own gate, re-downloaded up to sixteen megabytes from tarkov.dev, hashed it, and
// threw it away. It held nothing and saved nobody anything, and /catalog listed an empty array
// however many times it had been asked.
builder.Services.AddHttpClient(CatalogMirror.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    // [#317, RISK-EXTERNAL-DATA-BOUNDS / SER-RELAY-CATALOG-UNBOUNDED] A ceiling as well as a
    // clock. GetByteArrayAsync buffers whatever arrives, so a compromised or merely broken
    // upstream could hand this relay a body limited only by how fast it could send it for
    // thirty seconds. The desktop's own reader has had the same 32 MB ceiling for a while
    // (TarkovDevJsonClientOptions.MaximumResponseBytes); the real catalog is about half of it.
    client.MaxResponseContentBufferSize = 32L * 1024 * 1024;
    client.DefaultRequestHeaders.UserAgent.ParseAdd("TarkovCompanion-GroupServer/1.0");
});
builder.Services.AddSingleton<CatalogMirror>();
// The few hundred points a schematic can draw, derived from the mirror rather than fetched
// whole by the page. See Landmarks for the measurement that decided that.
builder.Services.AddHttpClient(Landmarks.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
    // [#317, RISK-EXTERNAL-DATA-BOUNDS / SER-RELAY-LANDMARKS-UNBOUNDED] Places are a few hundred
    // points per map; 8 MB is two orders of magnitude of headroom and still a ceiling.
    client.MaxResponseContentBufferSize = 8L * 1024 * 1024;
    client.DefaultRequestHeaders.UserAgent.ParseAdd("TarkovCompanion-GroupServer/1.0");
});
builder.Services.AddSingleton<Landmarks>();
// Answers "what is this worth" against the catalog the relay already holds, so a tablet asks
// a question rather than downloading 1.9 MB to answer it itself.
builder.Services.AddSingleton<ItemSearch>();

// Reports are taken and kept here; the hourly relay-watch workflow turns them into issues
// using the token GitHub Actions already gives it for its own repository. So this box holds no
// GitHub credential at all — which matters, because it is the internet-facing one.
builder.Services.AddSingleton<ProblemReports>();

// v2r-pairing-tablet: the paired-device pairing handshake mailbox (#277/#290). See
// CompanionPairingMailbox for what it does and does not do.
builder.Services.AddSingleton<CompanionPairingMailbox>();

// v2r-relay-owner (#278/#290): the relay's device registry, its owner-recovery secret, and the
// opaque-frame hub that routes a paired session's traffic once RecoverOwnerAsync/AddPairedDeviceAsync
// puts it there. Without TARKOV_RELAY_OWNER_RECOVERY_SECRET configured the registry still opens (on
// a random, never-exposed, never-reused secret) so its other operations degrade rather than fail to
// start, but RelayCompanionRoutes refuses the claim route outright — the same fail-closed shape
// RelayAdmin already uses for TARKOV_RELAY_ADMIN_KEY.
builder.Services.AddSingleton(provider => new OwnerRecoveryProtector(
    OwnerRecoverySecret() ?? RandomNumberGenerator.GetBytes(32),
    provider.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton(provider => RelayDeviceRegistry.OpenAsync(
        provider.GetRequiredService<TimeProvider>(),
        provider.GetRequiredService<OwnerRecoveryProtector>(),
        StorePath("relay-devices.json") is { } path ? new VerifiedRelayRegistryStore(path) : null)
    .AsTask().GetAwaiter().GetResult());
builder.Services.AddSingleton(provider => new OpaqueRelayFrameHub(
    provider.GetRequiredService<RelayDeviceRegistry>(),
    provider.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton(provider => new RelayOwnerClaimGate(provider.GetRequiredService<TimeProvider>()));
// [V2 rough package 24] The desktop's current map, held for its paired tablets.
builder.Services.AddSingleton(provider => new RelayMapSurfaceStore(
    provider.GetRequiredService<RelayDeviceRegistry>(),
    provider.GetRequiredService<TimeProvider>()));

var app = builder.Build();
var rooms = app.Services.GetRequiredService<GroupRooms>();
var marks = app.Services.GetRequiredService<GroupMarks>();
// v2r-fast-positions (package 31).
var roomChanges = app.Services.GetRequiredService<GroupRoomChanges>();
var registry = app.Services.GetRequiredService<GroupRoomRegistry>();
var timeProvider = app.Services.GetRequiredService<TimeProvider>();
// [#317] What a wrong key costs. See the middleware below.
var attempts = new RelayAttemptLimiter(timeProvider);
var relayOwnerRecoveryConfigured = OwnerRecoverySecret() is not null;
// [#553] One tenant per desktop. The registry, hub and map store above are the legacy tenant —
// relay-devices.json, exactly as an older relay wrote it — and every desktop that registers
// itself gets the same three of its own, kept in relay-desktops/ beside it.
var relayDesktops = RelayTenantDirectory.OpenAsync(
        timeProvider,
        app.Services.GetRequiredService<OwnerRecoveryProtector>(),
        app.Services.GetRequiredService<RelayDeviceRegistry>(),
        app.Services.GetRequiredService<OpaqueRelayFrameHub>(),
        app.Services.GetRequiredService<RelayMapSurfaceStore>(),
        StorePath("relay-desktops"))
    .AsTask().GetAwaiter().GetResult();
app.MapRelayCompanionRoutes(
    relayDesktops,
    relayOwnerRecoveryConfigured ? app.Services.GetRequiredService<OwnerRecoveryProtector>() : null,
    app.Services.GetRequiredService<RelayOwnerClaimGate>());

// [#562] Every map publish and every map read answered 500 for an hour on 2026-09-20 and nothing
// an operator looks at without SSHing in said so. First in the pipeline, so it also counts
// whatever the middleware below this answers (in practice never more than a 403/503/429).
var routeErrors = new RelayRouteErrorCounters();
app.Use(async (context, next) =>
{
    try
    {
        await next(context).ConfigureAwait(false);
    }
    finally
    {
        routeErrors.Observe(context);
    }
});

// Which rooms may be used at all.
//
// A room was whatever anybody's key hashed to, so anybody who could reach this relay could be
// in one. That is fine for a relay nobody else knows the address of and stops being fine the
// moment one does. Once an operator has registered a room, the rooms that are registered are
// the only ones this relay will serve.
//
// Before that, and with no operator secret set, nothing changes: an empty list means open. The
// alternative — closing the instant a secret is configured, before anything has been registered
// — would lock out every group on the relay at the moment the operator was trying to look at
// it.
//
// A key that is missing or too short is left alone here so the handler can answer it the way it
// always has. This decides which room may be used, not whether a key is a key.
app.Use(async (context, next) =>
{
    TryReadKey(context.Request, out var key);
    if (RelayAccess.Refuses(registry, context.Request.Path.Value, key))
    {
        // [#317] Two different refusals, said differently. "Not one of them" is an operator
        // decision; "could not be read" is a fault the operator has to go and fix, and reading
        // the first when the second is true has somebody looking for the wrong problem.
        context.Response.StatusCode = registry.IsUnreadable
            ? StatusCodes.Status503ServiceUnavailable
            : StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new
        {
            error = registry.IsUnreadable
                ? "This relay cannot read the list of rooms its operator registered, so it is serving none of them."
                : "This relay serves the rooms its operator registered, and this is not one of them.",
        }).ConfigureAwait(false);
        return;
    }

    await next(context).ConfigureAwait(false);
});

// [#317] What it costs to guess a key.
//
// Both keys were compared carefully and guessed freely: RISK-RELAY-KEY-BRUTEFORCE and
// RISK-ADMIN-KEY-BRUTEFORCE. Nothing counted a wrong one, so a relay on a public name answered
// guesses as fast as it could be asked. This counts the answers rather than the requests: a
// route that checked a key and said 401 or 403 is a failed attempt, anything else clears the
// caller's record, and a route with no key to check is not this decision's business.
//
// Placed after the room decision above so its 403 counts too, and before the handlers so the
// penalty is paid whatever they do.
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value;
    var checksAKey = RelayAccess.IsGroupPath(path) || RelayAccess.IsAdminPath(path);
    if (!checksAKey)
    {
        await next(context).ConfigureAwait(false);
        return;
    }

    var caller = RelayAttemptLimiter.CallerOf(context.Connection.RemoteIpAddress);
    if (attempts.IsRefused(caller, out var until))
    {
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers.RetryAfter =
            Math.Max(1, (int)Math.Ceiling((until - timeProvider.GetUtcNow()).TotalSeconds))
                .ToString(CultureInfo.InvariantCulture);
        await context.Response.WriteAsJsonAsync(new
        {
            error = "Too many keys have been refused from here recently. Try again shortly.",
        }).ConfigureAwait(false);
        return;
    }

    if (attempts.DelayFor(caller) is { Ticks: > 0 } delay)
    {
        try
        {
            await Task.Delay(delay, context.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Hanging up during the penalty is allowed and is not an error worth logging. It is
            // also not an escape: the caller's record is untouched, so the next attempt from that
            // address waits just as long.
            return;
        }
    }

    await next(context).ConfigureAwait(false);

    // Three outcomes, and only two of them are this limiter's business. A refusal is a wrong
    // key. A 2xx is a right one, and clears the penalty so a group that mispasted once is not
    // carrying it into the evening. Anything else — a 400, a 503 from the room registry above —
    // says nothing about whether the caller knows a key, so it neither counts nor clears.
    var status = context.Response.StatusCode;
    if (status is 401 or 403)
    {
        attempts.Record(caller, authorised: false);
    }
    else if (status is >= 200 and < 300)
    {
        attempts.Record(caller, authorised: true);
    }
});

// Members expired only when their room was read, so a room nobody reads never forgot
// anything, and an empty room was never removed at all. Bounded by time now rather than by
// whether anybody happens to look.
var sweeper = new PeriodicTimer(TimeSpan.FromMinutes(1));
_ = Task.Run(async () =>
{
    while (await sweeper.WaitForNextTickAsync().ConfigureAwait(false))
    {
        try
        {
            rooms.Sweep();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A failed sweep is one minute of memory, not a reason to stop sweeping.
        }
    }
});

// What this relay is, and how much it is holding. Counts only: never who, never where.
//
// It answered {status:"ok"} and nothing else, so there was no way to ask a running relay which
// build it was — not from a client, not from the updater that had just installed it, and not
// from anybody wondering whether a merge had actually reached it.
var build = typeof(GroupRooms).Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
    ?? "unknown";
var version = build.Split('+')[0];
// The first dot-separated piece of the metadata, because there are two commits in it.
//
// The workflow passes InformationalVersion=VERSION+SHA and the SDK appends the source revision
// on top, so /health reported the same forty characters twice with a dot between them. Nobody
// read it closely enough to notice until the admin panel started printing it.
var commit = build.Contains('+', StringComparison.Ordinal)
    ? build.Split('+')[1].Split('.')[0]
    : null;
var startedUtc = DateTimeOffset.UtcNow;

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    // What this server speaks. A client holding a different number can say so instead of
    // quietly missing a field and looking like a feature that does not work.
    protocol = GroupProtocol.Version,
    version,
    commit,
    startedUtc,
    rooms = rooms.RoomCount,
    members = rooms.MemberCount,
    // v2r-fast-positions (package 31): exchanges being held open for a change right now.
    held = roomChanges.WaitingCount,
    // [V2 rough package 34] And the same for the paired tablets' map reads, which are held by
    // the same rules against the same Kestrel. Counted separately because they are bounded
    // separately, and the only way to see either bound being reached is from outside.
    heldTabletReads = relayDesktops.WaitingCount,
    // [#562] route -> 5xx responses since start. Empty when nothing has failed.
    errors = routeErrors.Snapshot(),
}));

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

app.MapGet(PairedTransportBinding.TabletPagePath, () => Results.Content(Tablet.Page, "text/html; charset=utf-8"));

// v2r-tablet-marks-sync: the relay-frame sealing/opening the tablet page's live sync uses,
// served at an absolute path so it resolves the same from "/" and "/tablet". See Tablet.cs.
app.MapGet($"{PairedTransportBinding.TabletPagePath}/relay-crypto.js", () => Results.Content(Tablet.RelayCryptoScript, "text/javascript; charset=utf-8"));

// #290: how the tablet words the desktop's answer to a command. See Tablet.cs.
app.MapGet($"{PairedTransportBinding.TabletPagePath}/command-acknowledgement.js", () => Results.Content(Tablet.CommandAcknowledgementScript, "text/javascript; charset=utf-8"));

// v2r-pairing-tablet: the paired-device pairing handshake (#277/#290). The routes live in
// RelayPairingMailboxRoutes so a test can serve the mailbox this relay actually serves.
app.MapCompanionPairingMailboxRoutes();

// A member publishes themselves and is told about everyone else in one exchange, so there is
// no separate subscribe and no connection to hold open. A companion that is not running sends
// nothing and therefore shows nothing, which is the behaviour we want.
//
// The room is not in the URL any more. It is derived from the group's key, which is the only
// thing a member configures: see GroupKey for why one value replaced two.
//
// v2r-fast-positions (package 31): the handler lives in RelayRoomStateRoutes so a test can drive
// the route this relay actually serves. It also takes an optional hold — see that file.
app.MapGroupRoomState(rooms, marks, roomChanges);

// Reading the room without joining it.
//
// The exchange above is the companion's: it publishes and is answered in one round trip, which
// is right for something that has a position to contribute. A second screen has nothing to
// contribute -- it is not in the raid -- and joining as a member would put a phantom marker in
// the group and a phantom name in everybody's panel.
//
// So this returns everyone, including whoever is reading, because the reader is not one of
// them. Same key, because this is the same room and the key is the whole access model.
// One button, one issue. The person with the problem is the one who can see it and the least
// able to describe it, so the report travels instead of the conversation.
//
// Keyed like everything else: the rate limit counts per room rather than per person. That is
// not anonymity. The relay sees the key and the caller's address, and the body it keeps can name
// the reporter's display name and carry their folder paths and coordinates.
app.MapPost("/report", async Task<Results<Ok<ReportOutcome>, UnauthorizedHttpResult, BadRequest<string>>> (
    HttpRequest request,
    ProblemReports reports,
    CancellationToken cancellationToken) =>
{
    if (!TryReadKey(request, out var key))
    {
        return TypedResults.Unauthorized();
    }

    // Kestrel's own limit above is smaller than what this endpoint accepts, so without this
    // override every report over 32 KiB was refused with a 413 before this code ever ran,
    // whether or not it fit within ProblemReports.MaximumBytes.
    var sizeFeature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
    if (sizeFeature is { IsReadOnly: false })
    {
        sizeFeature.MaxRequestBodySize = ProblemReports.MaximumBytes;
    }

    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    if (string.IsNullOrWhiteSpace(body))
    {
        return TypedResults.BadRequest("A report needs a body.");
    }

    if (System.Text.Encoding.UTF8.GetByteCount(body) > ProblemReports.MaximumBytes)
    {
        return TypedResults.BadRequest($"A report may be at most {ProblemReports.MaximumBytes / 1024} KiB.");
    }

    var room = GroupKey.RoomFor(key);
    if (reports.IsRateLimited(room))
    {
        // Said plainly rather than refused silently: somebody pressing the button twice has a
        // reason, and being told the first one arrived is the useful answer.
        return TypedResults.BadRequest(
            $"This group has filed {ProblemReports.MaximumPerRoomPerHour} reports in the last hour. The earlier ones arrived.");
    }

    return TypedResults.Ok(reports.Accept(room, body));
});

// What is waiting to be turned into issues. References and sizes, never bodies: the repository
// is public, so an issue names a report and somebody comes here to read it.
//
// Behind an admin key rather than the group key, because any member of any group holds one of
// those and this lists every group's reports.
app.MapGet("/reports", Results<Ok<IReadOnlyList<StoredReport>>, UnauthorizedHttpResult> (
    HttpRequest request,
    ProblemReports reports) =>
{
    if (!RelayAdmin.IsAuthorised(request))
    {
        return TypedResults.Unauthorized();
    }

    return TypedResults.Ok(reports.List());
});

// One report, for whoever is holding the admin key and reading an issue that names it.
app.MapGet("/reports/{reference}", Results<Ok<string>, NotFound, UnauthorizedHttpResult> (
    string reference,
    HttpRequest request,
    ProblemReports reports) =>
{
    if (!RelayAdmin.IsAuthorised(request))
    {
        return TypedResults.Unauthorized();
    }

    return reports.Read(reference) is { } body ? TypedResults.Ok(body) : TypedResults.NotFound();
});

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

    var room = GroupKey.RoomFor(key);
    var added = marks.AddWaypoint(room, request.By, request.MapId, request.X, request.Y, request.Z, request.Label);
    // v2r-fast-positions (package 31): a mark is a change to the room, so a held exchange ends.
    roomChanges.Record(room, null);
    return TypedResults.Ok(added);
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

    var room = GroupKey.RoomFor(key);
    var added = marks.AddPing(room, request.By, request.MapId, request.X, request.Y, request.Z, request.Label);
    // v2r-fast-positions (package 31).
    roomChanges.Record(room, null);
    return TypedResults.Ok(added);
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

    var room = GroupKey.RoomFor(key);
    if (!marks.Complete(room, id, request.By))
    {
        return TypedResults.NotFound();
    }

    // v2r-fast-positions (package 31).
    roomChanges.Record(room, null);
    return TypedResults.Ok();
});

app.MapDelete("/waypoints/{id:long}", Results<Ok, NotFound, UnauthorizedHttpResult> (
    long id,
    HttpRequest http) =>
{
    if (!TryReadKey(http, out var key))
    {
        return TypedResults.Unauthorized();
    }

    var room = GroupKey.RoomFor(key);
    if (!marks.Remove(room, id))
    {
        return TypedResults.NotFound();
    }

    // v2r-fast-positions (package 31).
    roomChanges.Record(room, null);
    return TypedResults.Ok();
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

    var room = GroupKey.RoomFor(key);
    var cleared = marks.Clear(room, mapId, reachedOnly == true);
    // v2r-fast-positions (package 31).
    roomChanges.Record(room, null);
    return TypedResults.Ok(cleared);
});

app.MapDelete("/state/{name}", Results<Ok, UnauthorizedHttpResult> (
    string name,
    HttpRequest request) =>
{
    if (!TryReadKey(request, out var key))
    {
        return TypedResults.Unauthorized();
    }

    var room = GroupKey.RoomFor(key);
    rooms.Remove(room, name);
    // v2r-fast-positions (package 31): somebody leaving is what the others were waiting for.
    roomChanges.Record(room, null);
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

// What the second screen needs to be recognisable, and nothing else.
//
// No key: this is public game data, the same as /catalog, and requiring one would mean the
// tablet could not draw a map until somebody had typed a group key — which is the one screen
// where showing something before you are joined is worth having.
//
// Cached for an hour, which is how long the mirror holds a snapshot. Anything longer would
// serve landmarks from a catalog this server has already replaced.
// How landmarks go on the wire: the short names the record declares, and nothing written for
// a null. A lock has neither a name nor a faction, and there are more locks than anything else.
var landmarkJson = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};

// What an item is worth, for the second screen.
//
// No key, like /catalog and /landmarks: this is public game data, and the alternative is a
// tablet pulling the whole catalog to answer one question.
//
// The query is a search term and never a path, so there is nothing here for it to escape into.
app.MapGet("/search", async Task<IResult> (
    string? q,
    ItemSearch search,
    CancellationToken cancellationToken) =>
{
    var found = await search.FindAsync(q, cancellationToken).ConfigureAwait(false);
    return TypedResults.Ok(found);
});

app.MapGet("/landmarks", async (
    HttpRequest request,
    Landmarks landmarks,
    TimeProvider timeProvider,
    CancellationToken cancellationToken) =>
{
    var all = await landmarks.GetAsync(timeProvider, cancellationToken).ConfigureAwait(false);
    request.HttpContext.Response.Headers.CacheControl = "public, max-age=3600";
    // Nulls dropped rather than written. A lock has neither a name nor a faction, and there are
    // more locks than anything else.
    return TypedResults.Json(all, landmarkJson);
});

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
    //
    // The comparison tolerates a weak prefix. Cloudflare re-tags a response it compressed,
    // turning "abc" into W/"abc", and the client sends back what it was given — so a strict
    // comparison would miss every match the moment the cache in front of this started doing
    // its job, and every client would re-download 16 MB an hour for ever.
    if (request.Headers.IfNoneMatch.Any(value => Matches(value, snapshot.ETag)))
    {
        return TypedResults.StatusCode(StatusCodes.Status304NotModified);
    }

    var response = request.HttpContext.Response;
    response.Headers.ETag = snapshot.ETag;
    // An hour, which is how long the mirror holds a snapshot before asking upstream again.
    // Anything longer would serve a catalog this server has already replaced.
    response.Headers.CacheControl = "public, max-age=3600";
    // The tag names the catalog rather than the encoding, so a cache in front of this has to be
    // told that the two encodings are different responses.
    response.Headers.Vary = "Accept-Encoding";

    // Compressed at rest, so this is a header and a write rather than a compression per
    // request. 16,716,287 bytes becomes 1,344,177 — through a tunnel, per client, per hour.
    if (Accepts(request, "gzip"))
    {
        response.Headers.ContentEncoding = "gzip";
        return TypedResults.Bytes(snapshot.Gzip, "application/json");
    }

    return TypedResults.Bytes(snapshot.Body, "application/json");
});

// The operator's page.
//
// Behind the same secret as the endpoints it calls rather than behind a login, because this is
// one page for one person who runs one relay. It holds no secret itself: the key is typed into
// it and kept for the session in the browser, and every request carries it.
app.MapGet("/admin", () => Results.Content(AdminPanel.Page, "text/html; charset=utf-8"));

// What the relay is serving and what it was meant to be serving, side by side.
//
// The second half is the useful one. The relay is holding rooms right now, and until there was
// a list to compare them against there was no way to look at one and say whether it belonged.
app.MapGet("/admin/rooms", Results<Ok<AdminRoomsView>, UnauthorizedHttpResult> (HttpRequest request) =>
{
    if (!RelayAdmin.IsAuthorised(request))
    {
        return TypedResults.Unauthorized();
    }

    var occupancy = rooms.Occupancy();
    var registered = registry.List()
        .Select(room => new AdminRoom(room.Room, room.Label, room.CreatedUtc, occupancy.GetValueOrDefault(room.Room)))
        .ToArray();
    var known = registered.Select(room => room.Room).ToHashSet(StringComparer.Ordinal);
    var unregistered = occupancy
        .Where(entry => !known.Contains(entry.Key))
        .OrderByDescending(entry => entry.Value)
        .Select(entry => new AdminRoom(entry.Key, null, null, entry.Value))
        .ToArray();

    return TypedResults.Ok(new AdminRoomsView(
        registry.IsClosed,
        registry.IsUnreadable,
        version,
        commit,
        startedUtc,
        registered,
        unregistered));
});

// Registers a room: generating a key, adopting one the group already uses, or adopting a room
// this relay is already holding by its hash.
//
// A generated key is in the response to this request and is not persisted. The relay stores its
// hash, the same as it does for every other room, so there is no way to ask for it again. It is
// not relay-blind: every member request afterwards carries the key to this process in plaintext.
app.MapPost("/admin/rooms", Results<Ok<AdminRoomCreated>, UnauthorizedHttpResult, BadRequest<string>> (
    AdminRoomRequest body,
    HttpRequest request) =>
{
    if (!RelayAdmin.IsAuthorised(request))
    {
        return TypedResults.Unauthorized();
    }

    if (string.IsNullOrWhiteSpace(body.Label))
    {
        return TypedResults.BadRequest("A room needs a label, so the list means something later.");
    }

    if (body.Label.Length > 60)
    {
        return TypedResults.BadRequest("A label may be at most 60 characters.");
    }

    if (!string.IsNullOrWhiteSpace(body.Room))
    {
        var adopted = registry.Adopt(body.Room.Trim(), body.Label);
        return adopted is null
            ? TypedResults.BadRequest("That room is already registered, or the list is full.")
            : TypedResults.Ok(new AdminRoomCreated(adopted.Room, adopted.Label, null));
    }

    if (body.Key is not null && !GroupKey.IsAcceptable(body.Key))
    {
        return TypedResults.BadRequest(
            $"A key is between {GroupKey.MinimumLength} and {GroupKey.MaximumLength} characters.");
    }

    var created = registry.Add(body.Label, body.Key);
    return created is null
        ? TypedResults.BadRequest("That key is already registered, or the list is full.")
        : TypedResults.Ok(new AdminRoomCreated(created.Value.Room.Room, created.Value.Room.Label, created.Value.Key));
});

// Removes a room, and forgets whoever was in it.
//
// Both halves, because leaving the members behind would mean a room that is no longer allowed
// still had people visible in it until their entries expired on their own.
app.MapDelete("/admin/rooms/{room}", Results<Ok, NotFound, UnauthorizedHttpResult> (
    string room,
    HttpRequest request) =>
{
    if (!RelayAdmin.IsAuthorised(request))
    {
        return TypedResults.Unauthorized();
    }

    if (!registry.Remove(room))
    {
        return TypedResults.NotFound();
    }

    rooms.Clear(room);
    return TypedResults.Ok();
});

// Which build this relay is on against the one its signed release ring selects.
//
// The relay has updated itself every half hour for a while and could say nothing about it. A
// relay running an old build looked exactly like one running the newest, and one that installed
// a build, failed its health check and rolled back looked like both — the updater records the
// refusal so it does not loop, and nothing surfaced it.
app.MapGet("/admin/update", Results<Ok<RelayUpdateState>, UnauthorizedHttpResult> (
    HttpRequest request,
    RelayUpdate update) =>
{
    if (!RelayAdmin.IsAuthorised(request))
    {
        return TypedResults.Unauthorized();
    }

    return TypedResults.Ok(update.Read());
});

// Asks for one now rather than at the next tick.
//
// A file, not a command. This process runs unprivileged and must not be able to run one: it
// writes a marker in the state directory it already owns, and tarkov-group-update.path turns
// that into the same update the timer runs. The button is the timer's own path, half an hour
// early, with no new privilege anywhere.
app.MapPost("/admin/update", Results<Ok<RelayUpdateState>, BadRequest<string>, UnauthorizedHttpResult> (
    HttpRequest request,
    RelayUpdate update) =>
{
    if (!RelayAdmin.IsAuthorised(request))
    {
        return TypedResults.Unauthorized();
    }

    return update.Request()
        ? TypedResults.Ok(new RelayUpdateState(null, null, null, true, true, "Asked for. The relay restarts if there is a newer build."))
        : TypedResults.BadRequest("This relay cannot be asked to update: it has no writable state directory.");
});

// V2 rough package 36 (self-updating builds): the desktop's rough update channel, as read-only
// static files under /updates. No key, no state, nothing at startup; see UpdateFeedFiles.
app.MapUpdateFeed(UpdateFeedFiles.Root());

app.Run();

// The only thing checked here is that a key is long enough to be a key. In open mode there is no
// registered room verifier to compare it against: a key that nobody else uses simply names a room
// that nobody else is in. The relay still receives the plaintext bearer key before hashing it.
// Refusing a short one is not access control; it stops somebody believing "a" protects the group.
/// <summary>
/// Whether an If-None-Match value names this snapshot, weak prefix and all.
/// </summary>
/// <remarks>
/// Cloudflare re-tags a response it compressed itself, turning "abc" into W/"abc", and a client
/// sends back whatever it was given. A strict comparison therefore stops matching the moment a
/// cache in front of this starts working, and every client re-downloads the whole catalog every
/// hour for ever.
///
/// The tag is the SHA-256 of the identity bytes either way, so dropping the prefix compares the
/// thing that actually identifies the payload.
/// </remarks>
static bool Matches(string? candidate, string tag)
{
    if (string.IsNullOrWhiteSpace(candidate))
    {
        return false;
    }

    var trimmed = candidate.Trim();
    if (trimmed.StartsWith("W/", StringComparison.Ordinal))
    {
        trimmed = trimmed[2..];
    }

    return string.Equals(trimmed, tag, StringComparison.Ordinal);
}

/// <summary>Whether the client said it would take this encoding.</summary>
/// <remarks>
/// Read rather than assumed. The desktop client sets it, but the tablet's fetch and anything
/// somebody points at this by hand may not, and sending gzip to something that did not ask for
/// it is sending it something it cannot read.
/// </remarks>
static bool Accepts(HttpRequest request, string encoding) => request.Headers.AcceptEncoding
    .Any(value => value is not null &&
        value.Split(',').Any(part => part.Trim().StartsWith(encoding, StringComparison.OrdinalIgnoreCase)));

static bool TryReadKey(HttpRequest request, out string key) => GroupKey.TryRead(request, out key);

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
