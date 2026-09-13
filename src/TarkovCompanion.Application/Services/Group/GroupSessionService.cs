using System.Text;
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

    /// <summary>How long one exchange with the relay may take before it is abandoned.</summary>
    /// <remarks>
    /// The shared HttpClient is registered with Timeout.InfiniteTimeSpan, so a relay that
    /// accepts a connection and never answers parked this loop forever: the panel went on
    /// saying "Sharing as X · 3 others" over a snapshot that quietly aged, and nothing ever
    /// timed out to say otherwise.
    ///
    /// Eight seconds, against a five-second tick. Long enough that a slow-but-working relay is
    /// not cut off, short enough that a dead one is noticed within two ticks.
    /// </remarks>
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(8);

    /// <summary>
    /// How long a stale snapshot keeps showing the group before it gives them up.
    /// </summary>
    /// <remarks>
    /// Three minutes, matching GroupRooms.MemberLifetime on the server. Past that the relay
    /// would have dropped these members anyway, so continuing to draw them would be inventing
    /// a group rather than remembering one.
    /// </remarks>
    private static readonly TimeSpan StaleLimit = TimeSpan.FromMinutes(3);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IGroupSettingsStore _settings;
    private readonly IRuntimeStateStore _stateStore;
    private readonly HttpClient _httpClient;
    private readonly ILogger<GroupSessionService> _logger;
    private int _published;
    private GroupSnapshot? _lastGood;
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
        GroupQuestShare? quests = null,
        GroupKitShare? kits = null)
    {
        _settings = settings;
        _stateStore = stateStore;
        _httpClient = httpClient;
        _logger = logger;
        _quests = quests;
        _kits = kits;
    }

    private readonly GroupQuestShare? _quests;
    private readonly GroupKitShare? _kits;

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
                using var exchange = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                exchange.CancelAfter(ExchangeTimeout);
                await PublishOnceAsync(exchange.Token).ConfigureAwait(false);
            }
            // The filter tests the loop's own token rather than the exception's type. A
            // per-request timeout throws TaskCanceledException, which *is* an
            // OperationCanceledException, so the old filter would have let every timeout
            // escape, fault the worker and end sharing silently for the session — the trap
            // that made adding a timeout worse than not having one.
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
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
                PublishStale(detail);
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
        // A 401 from this server means the key failed GroupKey.IsAcceptable, which is a
        // length test: under eight characters or over 128. It cannot mean "wrong key" — the
        // key *is* the room, so a different key is a different room, which answers 200 with
        // nobody in it. Saying "wrong group key" sent people to compare keys that were fine.
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } =>
            $"The group key must be between {GroupKeyLimits.Minimum} and {GroupKeyLimits.Maximum} characters",
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
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
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
            // Deliberate, not a failure, so there is nothing to keep warm.
            _lastGood = null;
            Publish(settings.ResetReason is { Length: > 0 } reason
                ? GroupSnapshot.Off with { Detail = reason, UpdatedUtc = DateTimeOffset.UtcNow }
                : GroupSnapshot.Off);
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
        // What this game has said about the others, which is the one thing each of them cannot
        // read about themselves. Sent whenever sharing is on, because it is about the people
        // who asked to be in this group and it is the only route any of them has to their own
        // kit. The loadout switch governs what is said about the sender, not about others.
        var observed = _kits is null
            ? []
            : await _kits.GetAsync(cancellationToken).ConfigureAwait(false);
        var payload = Describe(snapshot, settings, sharedQuests, observed);
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
            // A 401 is an answer: the key is wrong and no amount of waiting fixes it, so the
            // group really is off. Everything else is the relay having a bad moment, and the
            // squad that was on the map a second ago should stay on it.
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _lastGood = null;
                Publish(GroupSnapshot.Off with
                {
                    Detail = $"The group key must be between {GroupKeyLimits.Minimum} and {GroupKeyLimits.Maximum} characters",
                    UpdatedUtc = DateTimeOffset.UtcNow,
                });
                return;
            }

            PublishStale($"Server answered {(int)response.StatusCode}");
            return;
        }

        var room = await response.Content.ReadFromJsonAsync<RoomStateDto>(Json, cancellationToken).ConfigureAwait(false);
        var seen = (room?.Members ?? [])
            .Select(member => (IReadOnlyList<ObservedKit>)(member.Observed ?? [])
                .Select(kit => new ObservedKit(kit.Name, kit.Loadout ?? []))
                .ToArray())
            .ToArray();
        // Each member's kit, from whoever could see it. Their own report wins where they have
        // one; otherwise it comes from the people whose game named it.
        var members = (room?.Members ?? [])
            .Select(member => Fill(Read(member), seen))
            .ToArray();
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

        var published = new GroupSnapshot(
            true,
            members,
            DescribeSharing(settings.DisplayName, members.Length, snapshot),
            DateTimeOffset.UtcNow)
        {
            // The one thing this companion cannot read about its own player, handed back by
            // the people whose game named it.
            MyLoadout = GroupKitMirror.Find(seen, settings.DisplayName),
            // The server expires pings for us, so whatever comes back is current by
            // definition and the client needs no timer of its own.
            Waypoints = (room?.Waypoints ?? []).Select(w =>
                new GroupWaypointView(w.Id, w.By, w.MapId, w.X, w.Y, w.Z, w.Label, w.CompletedBy)).ToArray(),
            Pings = (room?.Pings ?? []).Select(p =>
                new GroupPingView(p.Id, p.By, p.MapId, p.X, p.Y, p.Z, p.Label, p.CreatedUtc)).ToArray(),
        };
        // Kept so the next failed exchange has something true to keep showing. StaleSince is
        // null here by construction: this read worked, so nothing on screen is old.
        _lastGood = published;
        Publish(published);

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
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
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
        IReadOnlyList<string> sharedQuests,
        IReadOnlyList<ObservedKit> observed)
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
            sharedQuests)
        {
            // Published because a map with floors cannot place somebody without it, and the
            // waypoints beside them have carried one from the beginning.
            Y = position?.Position.Y,
            Observed = observed
                .Select(kit => new ObservedKitDto(kit.Name, kit.Loadout))
                .ToArray(),
            Trail = DescribeTrail(snapshot),
        };
    }

    /// <summary>
    /// The last few places this player has been, so the group can see a path rather than a dot.
    /// </summary>
    /// <remarks>
    /// The tail of the raid's own trail, which is already kept and already emptied when a raid
    /// begins — so a group trail cannot survive into the next raid and draw last raid's route
    /// on this raid's map.
    ///
    /// The current position is left out: it is published separately and would otherwise be
    /// drawn twice, once as a marker and once as the end of a line.
    ///
    /// Each point carries its own age rather than a timestamp. A clock that is wrong by an hour
    /// is common enough, and an age is a duration either end agrees on.
    /// </remarks>
    private static IReadOnlyList<TrailPointDto> DescribeTrail(ApplicationRuntimeSnapshot snapshot)
    {
        var trail = snapshot.Raid.PositionTrail;
        if (trail.Count < 2)
        {
            return [];
        }

        var now = DateTimeOffset.UtcNow;
        return
        [
            .. trail
                .Take(trail.Count - 1)
                .TakeLast(MaximumTrailPoints)
                .Select(step => new TrailPointDto(
                    step.Position.X,
                    step.Position.Z,
                    Math.Max(0, (now - step.Timestamp.ToUniversalTime()).TotalSeconds))
                {
                    Y = step.Position.Y,
                }),
        ];
    }

    /// <summary>How many places back is worth sending, which is what fits on a map.</summary>
    /// <remarks>
    /// Ten, against the server's bound of twelve. Five members with ten points each is fifty
    /// extra marks on a map whose whole design argument is that a map covered in markers
    /// answers nothing quickly.
    /// </remarks>
    private const int MaximumTrailPoints = 10;

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


    /// <summary>
    /// Fills in a member's kit from whoever could see it, when they could not see it themselves.
    /// </summary>
    private static GroupMemberView Fill(GroupMemberView member, IReadOnlyList<IReadOnlyList<ObservedKit>> seen) =>
        member.Loadout.Count > 0
            ? member
            : member with { Loadout = GroupKitMirror.Find(seen, member.Name) };

    private static GroupMemberView Read(MemberStateDto member) => new(
        member.Name,
        member.MapId,
        Enum.TryParse<RaidLifecycleState>(member.RaidState, out var state) ? state : RaidLifecycleState.Unknown,
        member.Side,
        member.X is { } x && member.Z is { } z ? new WorldPosition(x, member.Y ?? 0, z) : null,
        member.Heading,
        member.PositionAge is { } age ? TimeSpan.FromSeconds(age) : null,
        member.Loadout ?? [],
        member.Quests ?? [])
    {
        HasKnownHeight = member.Y is not null,
        Trail = (member.Trail ?? [])
            .Select(step => new GroupTrailPointView(
                step.X,
                step.Z,
                TimeSpan.FromSeconds(Math.Max(0, step.AgeSeconds)),
                step.Y))
            .ToArray(),
    };

    /// <summary>
    /// Keeps the last good picture of the group on screen, saying how old it is.
    /// </summary>
    /// <remarks>
    /// One failed exchange used to publish <see cref="GroupSnapshot.Off"/>, which empties
    /// Members, Waypoints and Pings; ApplySnapshot then cleared every squadmate and every mark
    /// from the map until the next tick five seconds later. A single dropped packet made the
    /// whole group disappear and come back.
    ///
    /// Past <see cref="StaleLimit"/> it gives them up, because by then the relay has dropped
    /// them too and drawing them would be inventing a group rather than remembering one.
    /// </remarks>
    private void PublishStale(string detail)
    {
        var now = DateTimeOffset.UtcNow;
        if (_lastGood is not { } good)
        {
            Publish(GroupSnapshot.Off with { Detail = detail, UpdatedUtc = now });
            return;
        }

        var since = good.StaleSince ?? now;
        if (now - since > StaleLimit)
        {
            _lastGood = null;
            Publish(GroupSnapshot.Off with { Detail = detail, UpdatedUtc = now });
            return;
        }

        var stale = good with
        {
            Detail = $"{detail} · last heard {Ago(now - since)} ago",
            StaleSince = since,
            UpdatedUtc = now,
        };
        _lastGood = stale;
        Publish(stale);
    }

    /// <summary>
    /// What this companion is sharing, and what it is not sharing yet.
    /// </summary>
    /// <remarks>
    /// "Sharing as Geo · 1 other" was the whole of it, and it is true while being useless: a
    /// client with no position publishes anyway — name, map and raid state go up with null
    /// coordinates — so a member appears in everybody's list with no marker on anybody's map,
    /// and the person it is happening to is told nothing at all.
    ///
    /// That happened to a second player on a fresh install, and the only way to find out why
    /// was for somebody else to notice the absence and ask. The client that has the problem is
    /// the one that can see the cause, so it says so.
    ///
    /// Only while something is actually wrong. Outside a raid there is no position to have,
    /// and nagging about it would make the normal state look broken.
    /// </remarks>
    public static string DescribeSharing(string? name, int others, ApplicationRuntimeSnapshot snapshot)
    {
        var sharing = others switch
        {
            0 => $"Sharing as {name} · nobody else here",
            1 => $"Sharing as {name} · 1 other",
            _ => $"Sharing as {name} · {others} others",
        };

        if (snapshot.Raid.LastKnownPosition is not null)
        {
            return sharing;
        }

        if (!snapshot.Observation.IsSupported)
        {
            return $"{sharing} · no position: watching the game is not supported here";
        }

        // The screenshot folder specifically, not "the folders". A position comes only from a
        // screenshot filename, so a companion watching the logs and not the screenshots knows
        // the map and the raid state — which is enough to appear in everybody's member list —
        // and has no position to put on anybody's map. That is exactly the shape of it when
        // the game writes its screenshots somewhere the default search does not look, which is
        // what OneDrive does, and Settings takes an explicit path for it.
        if (!snapshot.Observation.IsWatchingScreenshots)
        {
            return $"{sharing} · no position: the game's screenshot folder has not been found, set it in Settings";
        }

        if (snapshot.Raid.State != RaidLifecycleState.InRaid)
        {
            return sharing;
        }

        // Named, because "take a screenshot" is unhelpful to somebody who just did.
        //
        // Escape from Tarkov's screenshot key defaults to F12 and so does Steam's. A player who
        // added the game to Steam as a non-Steam shortcut has the overlay active, and the
        // overlay takes F12 before the game sees it: Steam writes a screenshot into its own
        // userdata folder, the game writes nothing, and this companion has a folder it is
        // watching correctly with nothing ever arriving in it.
        //
        // That is indistinguishable from "has not taken one yet" without saying so, and it
        // cost a player an evening. Rebinding either key, or turning the overlay off, fixes it.
        return HasBeenInRaidLongEnoughToExpectOne(snapshot)
            ? $"{sharing} · no screenshot has arrived this raid · if the game is on Steam, the overlay takes F12 before the game does — rebind the screenshot key or turn the overlay off"
            : $"{sharing} · no position yet: take a screenshot in the raid and it will be read";
    }

    /// <summary>
    /// Whether this raid has run long enough that a missing screenshot is worth explaining.
    /// </summary>
    /// <remarks>
    /// Two minutes. Long enough that somebody who meant to photograph something has had the
    /// chance, short enough to be useful while the raid is still on. Before that, "take a
    /// screenshot" is the honest answer and a paragraph about Steam would be noise.
    /// </remarks>
    private static bool HasBeenInRaidLongEnoughToExpectOne(ApplicationRuntimeSnapshot snapshot) =>
        snapshot.Raid.StartedUtc is { } started &&
        DateTimeOffset.UtcNow - started > TimeSpan.FromMinutes(2);

    /// <summary>How long ago, in the shortest form that is still honest.</summary>
    public static string Ago(TimeSpan elapsed) => elapsed < TimeSpan.FromMinutes(1)
        ? $"{Math.Max(0, (int)elapsed.TotalSeconds)}s"
        : $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s";

    /// <summary>
    /// Sends a diagnostic report to the relay, which files it where the work happens.
    /// </summary>
    /// <remarks>
    /// Here rather than in its own service because this is the one component that already
    /// knows the relay's address and holds the key, and a second thing that did would be a
    /// second thing to keep in step.
    ///
    /// The report is redacted before it gets here and nothing is added to it. What comes back
    /// is a sentence for the player and, when GitHub was reachable, a link.
    ///
    /// Every failure ends by pointing at Copy diagnostics, because the whole point is that the
    /// person with the problem can get the report out — and a relay they cannot reach is one
    /// of the problems they might be reporting.
    /// </remarks>
    public async Task<string> ReportProblemAsync(string report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (!settings.IsUsable)
        {
            return settings.MissingPiece is { } missing
                ? $"Cannot send: the group needs {missing}. Use Copy diagnostics instead."
                : "Cannot send: group sharing is not set up. Use Copy diagnostics instead.";
        }

        try
        {
            using var sending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            sending.CancelAfter(TimeSpan.FromSeconds(30));
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(new Uri(settings.ServerUri!), "report"))
            {
                Content = new StringContent(report, Encoding.UTF8, "text/markdown"),
            };
            request.Headers.Add("X-Group-Key", settings.Key!.Trim());
            using var response = await _httpClient.SendAsync(request, sending.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(sending.Token).ConfigureAwait(false);
                return $"The relay refused it ({(int)response.StatusCode}). {detail}";
            }

            var outcome = await response.Content
                .ReadFromJsonAsync<ReportOutcomeDto>(Json, sending.Token)
                .ConfigureAwait(false);
            return outcome is null
                ? "Sent · the relay took it."
                : $"Sent · reference {outcome.Reference} · {outcome.Detail}";
        }
        catch (Exception exception) when (exception is not OperationCanceledException || cancellationToken.IsCancellationRequested)
        {
            return $"Could not reach the relay: {Explain(exception)}. Use Copy diagnostics instead.";
        }
    }

    private sealed record ReportOutcomeDto(string Reference, string Detail);

    private void Publish(GroupSnapshot group) =>
        _stateStore.Update(current => current with { Group = group });

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // Said out loud rather than left to time out. DELETE /state/{name} has been served
        // since the relay was written and called by nothing, so a member who closed the
        // application stayed on everybody else's map for the full three-minute lifetime,
        // apparently still in the raid.
        //
        // Before the token is cancelled, because it uses it; and on its own short budget, so
        // a relay that has gone away cannot hold the application open while it closes.
        await LeaveRoomAsync().ConfigureAwait(false);
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

    /// <summary>Tells the relay this member is going, so the others stop drawing them.</summary>
    /// <remarks>
    /// Best effort and silent. Failing to say goodbye costs the group three minutes of a stale
    /// marker, which is exactly what happened every time before this; it must not cost anybody
    /// a hung close.
    /// </remarks>
    private async Task LeaveRoomAsync()
    {
        try
        {
            var settings = await _settings.GetAsync(CancellationToken.None).ConfigureAwait(false);
            if (!settings.IsUsable)
            {
                return;
            }

            using var leaving = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var request = new HttpRequestMessage(
                HttpMethod.Delete,
                new Uri(new Uri(settings.ServerUri!), $"state/{Uri.EscapeDataString(settings.DisplayName!.Trim())}"));
            request.Headers.Add("X-Group-Key", settings.Key!.Trim());
            using var response = await _httpClient.SendAsync(request, leaving.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Nothing to do about it and nobody to tell: the application is closing.
        }
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
        [property: JsonPropertyName("quests")] IReadOnlyList<string>? Quests)
    {
        /// <summary>How high they were standing, absent from clients that predate it.</summary>
        [JsonPropertyName("y")]
        public double? Y { get; init; }

        /// <summary>What this member's game said about everybody else in their party.</summary>
        [JsonPropertyName("observed")]
        public IReadOnlyList<ObservedKitDto>? Observed { get; init; }

        /// <summary>Where this member has been this raid, oldest first.</summary>
        [JsonPropertyName("trail")]
        public IReadOnlyList<TrailPointDto>? Trail { get; init; }
    }

    private sealed record TrailPointDto(
        [property: JsonPropertyName("x")] double X,
        [property: JsonPropertyName("z")] double Z,
        [property: JsonPropertyName("age")] double AgeSeconds)
    {
        [JsonPropertyName("y")]
        public double? Y { get; init; }
    }

    private sealed record ObservedKitDto(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("loadout")] IReadOnlyList<string>? Loadout);

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
