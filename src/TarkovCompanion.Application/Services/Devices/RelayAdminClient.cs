using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TarkovCompanion.Application.Services.Devices;

/// <summary>[#920] One live member of a relay room, as the relay owner sees it.</summary>
public sealed record RelayAdminMemberView(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("lastSeenUtc")] DateTimeOffset LastSeenUtc,
    [property: JsonPropertyName("drawings")] int Drawings);

/// <summary>[#920] One relay room: who is in it, when it last moved, and what it holds.</summary>
public sealed record RelayAdminRoomView(
    [property: JsonPropertyName("room")] string Room,
    [property: JsonPropertyName("label")] string? Label,
    [property: JsonPropertyName("lastActivityUtc")] DateTimeOffset? LastActivityUtc,
    [property: JsonPropertyName("waypoints")] int Waypoints,
    [property: JsonPropertyName("pings")] int Pings,
    [property: JsonPropertyName("members")] IReadOnlyList<RelayAdminMemberView>? Members,
    [property: JsonPropertyName("removed")] IReadOnlyList<string>? Removed);

/// <summary>What one admin call came to.</summary>
public enum RelayAdminCallOutcome
{
    Done,

    /// <summary>The relay does not count this desktop as its owner (401/403).</summary>
    NotOwner,

    /// <summary>A relay from before #920 (404/405): the controls do not exist there.</summary>
    Unsupported,
    Unreachable,
    Refused,
}

/// <summary>[#920] What an admin call answered; <see cref="Cleared"/> is how many things went.</summary>
public sealed record RelayAdminCallResult(RelayAdminCallOutcome Outcome, int Cleared = 0, int? Status = null);

/// <summary>
/// [#920] The relay owner's room controls, over the owner session <see cref="RelayMarksBridge"/>
/// keeps. The relay decides who the owner is; this only asks, and says what it was told.
/// </summary>
public sealed class RelayAdminClient(Func<HttpMethod, string, CancellationToken, Task<HttpResponseMessage?>> send)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public RelayAdminClient(RelayMarksBridge bridge)
        : this((bridge ?? throw new ArgumentNullException(nameof(bridge))).SendAsOwnerAsync)
    {
    }

    /// <summary>
    /// Whether the relay counts this desktop as its owner: true or false when it said so, null
    /// when it could not be asked.
    /// </summary>
    /// <remarks>
    /// [#936] This was false on any doubt, and the panel hid itself, its Refresh button included,
    /// on one 5xx, 429 or timeout, until the owner link happened to drop and come back. False now
    /// means an answer: a 200 that says so, a 401/403 (not the owner), a 404/405 (a relay from
    /// before the controls), or no owner session at all.
    /// </remarks>
    public async Task<bool?> IsRelayOwnerAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await send(HttpMethod.Get, "v2/companion/relay/admin-status", cancellationToken).ConfigureAwait(false);
            if (response is null)
            {
                return false;
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                or HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            {
                return false;
            }

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var json = JsonSerializer.Deserialize<JsonElement>(text);
            return json.ValueKind == JsonValueKind.Object &&
                json.TryGetProperty("relayOwner", out var owner) &&
                owner.ValueKind == JsonValueKind.True;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The relay's rooms, or null when they could not be read.</summary>
    public async Task<IReadOnlyList<RelayAdminRoomView>?> ReadRoomsAsync(CancellationToken cancellationToken)
    {
        var body = await GetAsync("admin/owner/rooms", cancellationToken).ConfigureAwait(false);
        if (body is not { } json || !json.TryGetProperty("rooms", out var rooms) || rooms.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return rooms.Deserialize<RelayAdminRoomView[]>(Json);
    }

    public Task<RelayAdminCallResult> ClearDrawingsAsync(string room, string? member, CancellationToken cancellationToken) =>
        PostAsync($"admin/owner/rooms/{Uri.EscapeDataString(room)}/drawings/clear{MemberQuery(member)}", cancellationToken);

    public Task<RelayAdminCallResult> ClearMarksAsync(string room, CancellationToken cancellationToken) =>
        PostAsync($"admin/owner/rooms/{Uri.EscapeDataString(room)}/marks/clear", cancellationToken);

    public Task<RelayAdminCallResult> RemoveMemberAsync(string room, string member, CancellationToken cancellationToken) =>
        PostAsync($"admin/owner/rooms/{Uri.EscapeDataString(room)}/members/remove{MemberQuery(member)}", cancellationToken);

    public Task<RelayAdminCallResult> ResetRoomAsync(string room, CancellationToken cancellationToken) =>
        PostAsync($"admin/owner/rooms/{Uri.EscapeDataString(room)}/reset", cancellationToken);

    private static string MemberQuery(string? member) =>
        member is null ? string.Empty : "?member=" + Uri.EscapeDataString(member);

    private async Task<JsonElement?> GetAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await send(HttpMethod.Get, path, cancellationToken).ConfigureAwait(false);
            if (response is null || !response.IsSuccessStatusCode)
            {
                return null;
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<JsonElement>(text);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private async Task<RelayAdminCallResult> PostAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await send(HttpMethod.Post, path, cancellationToken).ConfigureAwait(false);
            if (response is null)
            {
                return new(RelayAdminCallOutcome.NotOwner);
            }

            if (response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var cleared = JsonSerializer.Deserialize<JsonElement>(text).TryGetProperty("cleared", out var count) &&
                    count.TryGetInt32(out var value) ? value : 0;
                return new(RelayAdminCallOutcome.Done, cleared);
            }

            return new(
                response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => RelayAdminCallOutcome.NotOwner,
                    HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed => RelayAdminCallOutcome.Unsupported,
                    _ => RelayAdminCallOutcome.Refused,
                },
                Status: (int)response.StatusCode);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return new(RelayAdminCallOutcome.Unreachable);
        }
        catch (JsonException)
        {
            return new(RelayAdminCallOutcome.Done);
        }
    }
}
