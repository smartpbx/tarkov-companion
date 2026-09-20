using System.Text.Json;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.Application.Services.Devices;

/// <summary>This desktop's owner session on one relay, as the relay issued it.</summary>
public sealed record StoredRelayOwnerSession(
    string RelayOrigin,
    Guid SessionId,
    string Credential,
    DateTimeOffset ExpiresUtc);

/// <summary>What the desktop needs to keep talking to one already-approved device after a restart.</summary>
/// <param name="ReservedThroughSequence">
/// The highest desktop-to-tablet sender sequence this desktop may already have used. A relay frame's
/// AES-GCM nonce is its key epoch and sender sequence, so a restart that began again at one would
/// reuse a nonce under the same key and be refused as a replay besides. Sequences are reserved in
/// blocks and the reservation is what is stored, so a restart starts above anything ever sent.
/// </param>
public sealed record StoredPairedSession(
    Guid DeviceId,
    Guid SessionId,
    Guid ChannelId,
    long KeyEpoch,
    string TabletToDesktopKeyBase64,
    string DesktopToTabletKeyBase64,
    long ReservedThroughSequence);

/// <summary>
/// Keeps the desktop's half of the relay link across a restart, in the same OS-protected store the
/// TarkovTracker token uses (DPAPI on Windows).
/// </summary>
/// <remarks>
/// [#289, #290] The complaint was "you have to re-claim the relay on every restart". The owner
/// session and every paired session's traffic keys lived only in <see cref="RelayMarksBridge"/>'s
/// memory, so closing the desktop forgot the claim and cut every tablet off, and the only way back
/// was the relay's admin key and a full re-pair. What is stored is a session the relay issued and
/// can end, never the admin key. Where protected storage is unavailable (anything but Windows)
/// every method here does nothing, which leaves the old memory-only behaviour rather than writing
/// secrets in the clear.
/// </remarks>
public sealed class RelayLinkVault
{
    private const string OwnerGeneration = "relay-owner-v1";
    private const string SessionGeneration = "relay-session-v1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IIntegrationSecretStore _secrets;

    public RelayLinkVault(IIntegrationSecretStore secrets)
    {
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
    }

    public bool IsAvailable => _secrets.IsAvailable;

    public Task SaveOwnerAsync(
        CompanionDeviceId desktopDeviceId,
        StoredRelayOwnerSession owner,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return SaveAsync(OwnerReference(desktopDeviceId), owner, cancellationToken);
    }

    /// <summary>The stored owner session for this relay, or null when there is none for it.</summary>
    public async Task<StoredRelayOwnerSession?> LoadOwnerAsync(
        CompanionDeviceId desktopDeviceId,
        Uri relayOrigin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relayOrigin);
        var stored = await LoadAsync<StoredRelayOwnerSession>(OwnerReference(desktopDeviceId), cancellationToken)
            .ConfigureAwait(false);
        // A claim belongs to the relay it was made on. After the relay address changes, the old
        // session is somebody else's credential and must never be sent to the new host.
        return stored is not null &&
               string.Equals(stored.RelayOrigin, Normalize(relayOrigin), StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(stored.Credential) &&
               stored.SessionId != Guid.Empty
            ? stored
            : null;
    }

    public Task ForgetOwnerAsync(CompanionDeviceId desktopDeviceId, CancellationToken cancellationToken = default) =>
        DeleteAsync(OwnerReference(desktopDeviceId), cancellationToken);

    public Task SaveSessionAsync(StoredPairedSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return SaveAsync(SessionReference(session.SessionId), session, cancellationToken);
    }

    public Task<StoredPairedSession?> LoadSessionAsync(DeviceSessionId sessionId, CancellationToken cancellationToken = default) =>
        LoadAsync<StoredPairedSession>(SessionReference(sessionId.Value), cancellationToken);

    public Task ForgetSessionAsync(DeviceSessionId sessionId, CancellationToken cancellationToken = default) =>
        DeleteAsync(SessionReference(sessionId.Value), cancellationToken);

    public static string Normalize(Uri relayOrigin)
    {
        ArgumentNullException.ThrowIfNull(relayOrigin);
        return relayOrigin.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    private async Task SaveAsync<T>(IntegrationSecretReference reference, T value, CancellationToken cancellationToken)
    {
        if (!_secrets.IsAvailable)
        {
            return;
        }

        try
        {
            await _secrets.SaveAsync(reference, JsonSerializer.Serialize(value, Json), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            // The link still works for this run; it simply will not survive the next restart.
        }
    }

    private async Task<T?> LoadAsync<T>(IntegrationSecretReference reference, CancellationToken cancellationToken)
        where T : class
    {
        if (!_secrets.IsAvailable)
        {
            return null;
        }

        try
        {
            var json = await _secrets.LoadAsync(reference, cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<T>(json, Json);
        }
        catch (Exception exception) when (IsStorageFailure(exception) || exception is JsonException)
        {
            return null;
        }
    }

    private async Task DeleteAsync(IntegrationSecretReference reference, CancellationToken cancellationToken)
    {
        if (!_secrets.IsAvailable)
        {
            return;
        }

        try
        {
            await _secrets.DeleteAsync(reference, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            // Nothing to recover: a secret that cannot be deleted is also one the relay has
            // already been told to stop honouring.
        }
    }

    private static bool IsStorageFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException
            or System.ComponentModel.Win32Exception or System.Security.Cryptography.CryptographicException;

    // The store keys a secret by (kind, profile id, game mode, generation). A relay link belongs
    // to the desktop rather than to a game profile, so the id is the desktop's own device id (or
    // the session's) and the game mode is a fixed placeholder.
    private static IntegrationSecretReference OwnerReference(CompanionDeviceId desktopDeviceId) => new(
        IntegrationSecretKind.RelayOwnerSession,
        desktopDeviceId.Value,
        GameMode.Regular,
        OwnerGeneration);

    private static IntegrationSecretReference SessionReference(Guid sessionId) => new(
        IntegrationSecretKind.PairedDeviceSession,
        sessionId,
        GameMode.Regular,
        SessionGeneration);
}
