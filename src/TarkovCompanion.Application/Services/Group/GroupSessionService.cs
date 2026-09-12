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
        ILogger<GroupSessionService> logger)
    {
        _settings = settings;
        _stateStore = stateStore;
        _httpClient = httpClient;
        _logger = logger;
    }

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
            "The group server refused the secret. Everyone in a group has to type the same one.",
        HttpRequestException { StatusCode: HttpStatusCode.BadRequest } =>
            "The group server rejected the room or display name. Both are required, and neither may be long.",
        HttpRequestException { StatusCode: { } status } =>
            $"The group server answered {(int)status}.",
        HttpRequestException =>
            $"The group server could not be reached: {exception.Message}",
        TaskCanceledException =>
            "The group server did not answer in time.",
        _ => $"Sharing failed: {exception.Message}",
    };

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
                Detail = $"Sharing is on but needs {settings.MissingPiece}.",
                UpdatedUtc = DateTimeOffset.UtcNow,
            });
            return;
        }

        var snapshot = _stateStore.Current;
        var payload = Describe(snapshot, settings);
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
                    ? "The group server rejected the shared secret."
                    : $"The group server answered {(int)response.StatusCode}.",
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
            _logger.LogInformation(
                "Group publish {Count} succeeded as {Name} in room {Room}; {Members} other member(s) present.",
                _published,
                settings.DisplayName,
                settings.Room,
                members.Length);
        }

        Publish(new(
            true,
            members,
            members.Length switch
            {
                0 => $"Sharing as {settings.DisplayName}. Nobody else has this key open right now.",
                1 => $"Sharing as {settings.DisplayName}. One other person sharing.",
                var count => $"Sharing as {settings.DisplayName}. {count} others sharing.",
            },
            DateTimeOffset.UtcNow));
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
    private static MemberStateDto Describe(ApplicationRuntimeSnapshot snapshot, GroupSharingSettings settings)
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
            settings.SharesQuests ? DescribeQuests(snapshot) : []);
    }

    /// <summary>
    /// What the player is carrying, as far as the companion knows it.
    /// </summary>
    /// <remarks>
    /// The game writes the player's own inventory nowhere the companion can read, which
    /// docs/research/EFT_LOG_FACTS.md records in full, so there is nothing honest to send yet.
    /// The switch exists and sends an empty list rather than pretending, because the day the
    /// player builds a kit by hand on the loadout page this is where it will come from.
    /// </remarks>
    private static IReadOnlyList<string> DescribeLoadout(ApplicationRuntimeSnapshot snapshot) => [];

    private static IReadOnlyList<string> DescribeQuests(ApplicationRuntimeSnapshot snapshot) => [];

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
        [property: JsonPropertyName("members")] IReadOnlyList<MemberStateDto> Members);
}
