using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Application.Services.Group;

/// <summary>
/// Publishes this player to their group and reports back who else is there.
/// </summary>
/// <remarks>
/// This is the one part of the companion that sends anything anywhere, and it is off until
/// somebody turns it on. What it sends is assembled in one method below, so the promise made
/// in the interface can be checked against the code rather than taken on trust.
///
/// It publishes on a slow tick rather than on every change. A raid produces state changes
/// several times a second and the group does not need to see any of them at that rate; what
/// they want is roughly where somebody is, which is a question a few seconds old answers just
/// as well.
///
/// A failure never interrupts anything. The group view is an extra, and losing it must not
/// cost the player their map.
/// </remarks>
public sealed class GroupSessionService : IAsyncDisposable
{
    /// <summary>How often this player is published and the group re-read.</summary>
    /// <remarks>
    /// Five seconds. Fast enough that a squadmate's marker feels current, slow enough that a
    /// group of six is a trivial amount of traffic for a small self-hosted service.
    /// </remarks>
    private static readonly TimeSpan PublishInterval = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IGroupSettingsStore _settings;
    private readonly IRuntimeStateStore _stateStore;
    private readonly HttpClient _httpClient;
    private readonly ILogger<GroupSessionService> _logger;
    private int _published;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _worker;
    private bool _disposed;

    public GroupSessionService(
        IGroupSettingsStore settings,
        IRuntimeStateStore stateStore,
        HttpClient httpClient,
        ILogger<GroupSessionService> logger,
        // Optional so a composition without quest storage still shares a position, which is
        // what every test that builds this by hand relies on.
        GroupQuestShare? quests = null)
    {
        _settings = settings;
        _stateStore = stateStore;
        _httpClient = httpClient;
        _logger = logger;
        _quests = quests;
    }

    private readonly GroupQuestShare? _quests;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _worker ??= Task.Run(() => RunAsync(_stopping.Token));
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await PublishOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Deliberately swallowed after reporting. The group is an extra; a server that
                // is down must not take the map with it.
                //
                // Reported to the log as well as to the interface, which it was not. Sharing
                // wrote no line of any kind, so when a member's state was being refused there
                // was nothing to read: the whole diagnosis had to come from reading a config
                // file on the machine and probing the server from outside. Every other part of
                // this application says what it did; this one was silent.
                var detail = Explain(exception);
                _logger.LogWarning(exception, "Group publish failed: {Detail}", detail);
                Publish(GroupSnapshot.Off with
                {
                    Detail = detail,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                });
            }

            try
            {
                await Task.Delay(PublishInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Says what went wrong in terms of what to do about it.
    /// </summary>
    /// <remarks>
    /// "The group server could not be reached" was the message for every failure including the
    /// one that actually happened, which was that the server understood perfectly and refused
    /// the credentials. Somebody reading that goes looking at their network, and the server is
    /// fine and the network is fine and the thing that is wrong is a value they typed.
    /// </remarks>
    private static string Explain(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } =>
            "Wrong group key · everyone has to type the same one",
        HttpRequestException { StatusCode: HttpStatusCode.BadRequest } =>
            "Server rejected the key or the display name",
        HttpRequestException { StatusCode: { } status } =>
            $"Server answered {(int)status}",
        HttpRequestException =>
            $"Server unreachable · {exception.Message}",
        TaskCanceledException =>
            "Server did not answer in time",
        _ => $"Sharing failed · {exception.Message}",
    };

    /// <summary>
    /// Marks a place for the group: a waypoint that stays, or a ping that fades.
    /// </summary>
    /// <remarks>
    /// Sent immediately rather than folded into the next tick. A ping is somebody saying "look
    /// here" and five seconds of silence is long enough for that to stop being useful, which
    /// is the same reason the server expires them quickly.
    ///
    /// Nothing is drawn locally in response. The mark comes back on the very next exchange
    /// like everybody else's, so one code path draws every mark and a sender never sees a
    /// version of the group's state that the group does not have.
    /// </remarks>
    public async Task<bool> MarkAsync(
        string mapId,
        WorldPosition position,
        string? label,
        bool isPing,
        CancellationToken cancellationToken)
    {
        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (!settings.IsUsable || string.IsNullOrWhiteSpace(mapId))
        {
            return false;
        }

        try
        {
            var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(new Uri(settings.ServerUri!), isPing ? "pings" : "waypoints"))
            {
                Content = JsonContent.Create(new MarkDto(
                    settings.DisplayName!.Trim(), mapId, position.X, position.Y, position.Z, label)),
            };
            request.Headers.Add("X-Group-Key", settings.Key!.Trim());
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            _logger.LogInformation(
                "Marked {Kind} on {Map} for the group.", isPing ? "a ping" : "a waypoint", mapId);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Could not mark a place for the group.");
            return false;
        }
    }

    private async Task PublishOnceAsync(CancellationToken cancellationToken)
    {
        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (!settings.IsEnabled)
        {
            Publish(GroupSnapshot.Off);
            return;
        }

        if (!settings.IsUsable)
        {
            Publish(GroupSnapshot.Off with
            {
                Detail = $"Needs {settings.MissingPiece}",
                UpdatedUtc = DateTimeOffset.UtcNow,
            });
            return;
        }

        var snapshot = _stateStore.Current;
        // Read before the payload is assembled, and cached for a minute inside, because the
        // publish loop runs every few seconds and a quest board does not.
        var sharedQuests = settings.SharesQuests && _quests is not null
            ? await _quests.GetAsync(cancellationToken).ConfigureAwait(false)
            : [];
        var payload = Describe(snapshot, settings, sharedQuests);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(new Uri(settings.ServerUri!), "state"))
        {
            Content = JsonContent.Create(payload, options: Json),
        };
        request.Headers.Add("X-Group-Key", settings.Key!.Trim());

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            Publish(GroupSnapshot.Off with
            {
                Detail = response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    ? "Wrong group key"
                    : $"Server answered {(int)response.StatusCode}",
                UpdatedUtc = DateTimeOffset.UtcNow,
            });
            return;
        }

        var room = await response.Content.ReadFromJsonAsync<RoomStateDto>(Json, cancellationToken).ConfigureAwait(false);
        var members = (room?.Members ?? []).Select(Read).ToArray();
        // Occasionally, not every five seconds. Three lines at the start answer "is it working
        // at all", which is the question, and one every ten minutes after that shows it still
        // is, without filling an evening's log.
        if (Interlocked.Increment(ref _published) is 1 or 2 or 3 || _published % 120 == 0)
        {
            // The key is never logged. It is the only thing protecting the group now that it
            // is also the room, and a log file is the easiest place to read one out of.
            _logger.LogInformation(
                "Group publish {Count} succeeded as {Name}; {Members} other member(s) present.",
                _published,
                settings.DisplayName,
                members.Length);
        }

        Publish(new GroupSnapshot(
            true,
            members,
            members.Length switch
            {
                0 => $"Sharing as {settings.DisplayName} · nobody else here",
                1 => $"Sharing as {settings.DisplayName} · 1 other",
                var count => $"Sharing as {settings.DisplayName} · {count} others",
            },
            DateTimeOffset.UtcNow)
        {
            // The server expires pings for us, so whatever comes back is current by
            // definition and the client needs no timer of its own.
            Waypoints = (room?.Waypoints ?? []).Select(w =>
                new GroupWaypointView(w.Id, w.By, w.MapId, w.X, w.Y, w.Z, w.Label, w.CompletedBy)).ToArray(),
            Pings = (room?.Pings ?? []).Select(p =>
                new GroupPingView(p.Id, p.By, p.MapId, p.X, p.Y, p.Z, p.Label, p.CreatedUtc)).ToArray(),
        });

        await CompleteReachedAsync(room, snapshot, settings, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Ticks off any waypoint the player is standing on.
    /// </summary>
    /// <remarks>
    /// A waypoint somebody reached is drawn quiet rather than removed, because "we went there"
    /// is worth keeping on screen, and until now nothing ever decided that anybody had. The
    /// group's own plan was a list that only ever grew.
    ///
    /// Decided here rather than on the server, because the server is never told where anybody
    /// is except as the two coordinates a member publishes, and it has no business measuring
    /// distances between people and places. The client knows its own screenshot position and
    /// says so once.
    ///
    /// The state that comes back is a moment old, so the same waypoint can be reported twice
    /// before the next exchange catches up. The server refuses the second, which is why that
    /// answer is not treated as a failure.
    /// </remarks>
    private async Task CompleteReachedAsync(
        RoomStateDto? room,
        ApplicationRuntimeSnapshot snapshot,
        GroupSharingSettings settings,
        CancellationToken cancellationToken)
    {
        var raid = snapshot.Raid;
        if (room?.Waypoints is not { Count: > 0 } waypoints ||
            raid.MapId is not { Length: > 0 } mapId ||
            raid.LastKnownPosition is not { } position)
        {
            return;
        }

        foreach (var waypoint in waypoints)
        {
            if (waypoint.CompletedBy is not null ||
                !string.Equals(waypoint.MapId, mapId, StringComparison.OrdinalIgnoreCase) ||
                !GroupWaypointReach.IsReached(position.Position, waypoint.X, waypoint.Y, waypoint.Z))
            {
                continue;
            }

            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    new Uri(new Uri(settings.ServerUri!), $"waypoints/{waypoint.Id}/reached"))
                {
                    Content = JsonContent.Create(new ReachedDto(settings.DisplayName!.Trim()), options: Json),
                };
                request.Headers.Add("X-Group-Key", settings.Key!.Trim());
                using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Reached waypoint {Id} on {Map}.", waypoint.Id, mapId);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One waypoint left unticked, and the next exchange tries again.
                _logger.LogDebug(exception, "Could not report reaching waypoint {Id}.", waypoint.Id);
            }
        }
    }


    /// <summary>
    /// Assembles everything this companion sends, in one place.
    /// </summary>
    /// <remarks>
    /// Deliberately one method and deliberately explicit. Somebody asking "what does this send
    /// about me" deserves an answer they can read, and the answer is this and nothing else.
    /// Loadout and quests are each behind their own switch, so agreeing to share a position is
    /// not agreeing to share a kit.
    /// </remarks>
    private static MemberStateDto Describe(
        ApplicationRuntimeSnapshot snapshot,
        GroupSharingSettings settings,
        IReadOnlyList<string> sharedQuests)
    {
        var raid = snapshot.Raid;
        var position = raid.LastKnownPosition;
        return new(
            settings.DisplayName!,
            raid.MapId,
            raid.State.ToString(),
            raid.Side,
            position?.Position.X,
            position?.Position.Z,
            position?.HeadingDegrees,
            position is null ? null : (DateTimeOffset.UtcNow - position.Timestamp.ToUniversalTime()).TotalSeconds,
            settings.SharesLoadout ? DescribeLoadout(snapshot) : [],
            sharedQuests);
    }

    /// <summary>
    /// What the player is carrying, as far as the companion knows it.
    /// </summary>
    /// <remarks>
    /// The game writes the player's own inventory nowhere the companion can read, which
    /// docs/research/EFT_LOG_FACTS.md records in full, so there is still nothing honest to
    /// send. A squadmate running a companion that reads its quick bar out of a screenshot does
    /// share a kit, which is why one member of a group can show one and another cannot; that
    /// is the same reading, from the same picture, that #35 is for.
    ///
    /// The switch sends an empty list rather than pretending. The Group page says so beside
    /// it, because a switch that silently does nothing is worse than one that is not there.
    /// </remarks>
    private static IReadOnlyList<string> DescribeLoadout(ApplicationRuntimeSnapshot snapshot) => [];


    private static GroupMemberView Read(MemberStateDto member) => new(
        member.Name,
        member.MapId,
        Enum.TryParse<RaidLifecycleState>(member.RaidState, out var state) ? state : RaidLifecycleState.Unknown,
        member.Side,
        member.X is { } x && member.Z is { } z ? new WorldPosition(x, 0, z) : null,
        member.Heading,
        member.PositionAge is { } age ? TimeSpan.FromSeconds(age) : null,
        member.Loadout ?? [],
        member.Quests ?? []);

    private void Publish(GroupSnapshot group) =>
        _stateStore.Update(current => current with { Group = group });

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_worker is { } worker)
        {
            try
            {
                await worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stopping.Dispose();
    }

    private sealed record MemberStateDto(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("mapId")] string? MapId,
        [property: JsonPropertyName("raidState")] string RaidState,
        [property: JsonPropertyName("side")] string? Side,
        [property: JsonPropertyName("x")] double? X,
        [property: JsonPropertyName("z")] double? Z,
        [property: JsonPropertyName("heading")] double? Heading,
        [property: JsonPropertyName("positionAge")] double? PositionAge,
        [property: JsonPropertyName("loadout")] IReadOnlyList<string>? Loadout,
        [property: JsonPropertyName("quests")] IReadOnlyList<string>? Quests);

    private sealed record RoomStateDto(
        [property: JsonPropertyName("room")] string Room,
        [property: JsonPropertyName("members")] IReadOnlyList<MemberStateDto> Members)
    {
        [JsonPropertyName("waypoints")]
        public IReadOnlyList<WaypointDto> Waypoints { get; init; } = [];

        [JsonPropertyName("pings")]
        public IReadOnlyList<PingDto> Pings { get; init; } = [];
    }

    private sealed record ReachedDto([property: JsonPropertyName("by")] string By);

    private sealed record MarkDto(
        [property: JsonPropertyName("by")] string By,
        [property: JsonPropertyName("mapId")] string MapId,
        [property: JsonPropertyName("x")] double X,
        [property: JsonPropertyName("y")] double Y,
        [property: JsonPropertyName("z")] double Z,
        [property: JsonPropertyName("label")] string? Label);

    private sealed record WaypointDto(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("by")] string By,
        [property: JsonPropertyName("mapId")] string MapId,
        [property: JsonPropertyName("x")] double X,
        [property: JsonPropertyName("y")] double Y,
        [property: JsonPropertyName("z")] double Z,
        [property: JsonPropertyName("label")] string? Label,
        [property: JsonPropertyName("completedBy")] string? CompletedBy);

    private sealed record PingDto(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("by")] string By,
        [property: JsonPropertyName("mapId")] string MapId,
        [property: JsonPropertyName("x")] double X,
        [property: JsonPropertyName("y")] double Y,
        [property: JsonPropertyName("z")] double Z,
        [property: JsonPropertyName("label")] string? Label,
        [property: JsonPropertyName("createdUtc")] DateTimeOffset CreatedUtc);
}
