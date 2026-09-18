using System.Security.Cryptography;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.GroupServer.Security;
using TarkovCompanion.GroupServer.Storage;

namespace TarkovCompanion.GroupServer;

/// <summary>What the relay is holding for the paired tablets of one desktop.</summary>
public sealed record RelayMapSurfaceEntry(
    CompanionDeviceId OwnerDeviceId,
    byte[] SurfaceJson,
    string? ArtworkMediaType,
    string? ArtworkSha256,
    byte[]? Artwork,
    DateTimeOffset PublishedUtc);

public sealed record RelayMapSurfaceResult(bool Accepted, string? Code)
{
    public static RelayMapSurfaceResult Ok { get; } = new(true, null);

    public static RelayMapSurfaceResult Reject(string code) => new(false, code);
}

/// <summary>
/// The desktop's current map, held for its paired tablets.
/// </summary>
/// <remarks>
/// [V2 rough package 24, #407] Artwork does not fit the sealed frame transport — a relay payload
/// root is bounded at 64 KiB and a rasterized map plan is megabytes — so the reviewed picture
/// travels as its own authenticated resource, which is the route the issue names first. The relay
/// holds it opaquely: it never parses the surface, never knows which map it is, and hands it only
/// to a session it has just authenticated against its own registry, so a revoked device reads
/// nothing (its credential no longer authenticates at all).
///
/// One desktop, one surface. This relay serves one owner in practice, and holding a surface per
/// owner would let a second desktop's artwork be read by the first desktop's tablets, which is
/// exactly the confusion the single slot prevents: a publish from a different owner replaces it.
///
/// In memory only, and deliberately not persisted. It is a picture of what the desktop is showing
/// right now; a relay that restarts has nothing to say until the desktop publishes again, which it
/// does on its next scene rebuild.
/// </remarks>
public sealed class RelayMapSurfaceStore
{
    public const int MaximumSurfaceBytes = 1024 * 1024;

    /// <summary>A rasterized plan at the desktop's own composition limit, with room to spare.</summary>
    public const int MaximumArtworkBytes = 24 * 1024 * 1024;

    private static readonly IReadOnlySet<string> AllowedMediaTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "image/png",
        "image/jpeg",
        "image/webp",
    };

    private readonly RelayDeviceRegistry _registry;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();
    private RelayMapSurfaceEntry? _entry;

    public RelayMapSurfaceStore(RelayDeviceRegistry registry, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _registry = registry;
        _timeProvider = timeProvider;
    }

    /// <summary>The desktop replaces what its tablets are drawing. Owner only.</summary>
    public RelayMapSurfaceResult Publish(RelayPrincipal principal, ReadOnlySpan<byte> surfaceJson)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (!CanPublish(principal))
        {
            return RelayMapSurfaceResult.Reject("not-authorized");
        }

        if (surfaceJson.Length is 0 or > MaximumSurfaceBytes)
        {
            return RelayMapSurfaceResult.Reject("surface-rejected");
        }

        lock (_gate)
        {
            // The artwork is kept across a surface publish when the surface still names it: the
            // desktop republishes its scene far more often than its picture changes, and
            // re-uploading megabytes per raid tick is the transfer this split exists to avoid.
            var previous = _entry;
            var keepArtwork = previous is not null && previous.OwnerDeviceId == principal.DeviceId;
            _entry = new RelayMapSurfaceEntry(
                principal.DeviceId,
                surfaceJson.ToArray(),
                keepArtwork ? previous!.ArtworkMediaType : null,
                keepArtwork ? previous!.ArtworkSha256 : null,
                keepArtwork ? previous!.Artwork : null,
                _timeProvider.GetUtcNow());
        }

        return RelayMapSurfaceResult.Ok;
    }

    /// <summary>The reviewed picture the current surface names. Owner only.</summary>
    public RelayMapSurfaceResult PublishArtwork(
        RelayPrincipal principal,
        string mediaType,
        string contentSha256,
        ReadOnlySpan<byte> artwork)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (!CanPublish(principal))
        {
            return RelayMapSurfaceResult.Reject("not-authorized");
        }

        if (string.IsNullOrWhiteSpace(mediaType) || !AllowedMediaTypes.Contains(mediaType))
        {
            return RelayMapSurfaceResult.Reject("media-type-rejected");
        }

        if (artwork.Length is 0 or > MaximumArtworkBytes)
        {
            return RelayMapSurfaceResult.Reject("artwork-rejected");
        }

        // The hash the caller declares is the one the surface names, so a picture that does not
        // match it would be drawn under another asset's attribution and content hash. Checked
        // here rather than trusted, because this is the only place both are in hand.
        var actual = Convert.ToHexStringLower(SHA256.HashData(artwork));
        if (!string.Equals(actual, contentSha256, StringComparison.OrdinalIgnoreCase))
        {
            return RelayMapSurfaceResult.Reject("artwork-hash-mismatch");
        }

        lock (_gate)
        {
            if (_entry is not { } current || current.OwnerDeviceId != principal.DeviceId)
            {
                return RelayMapSurfaceResult.Reject("no-surface");
            }

            _entry = current with
            {
                ArtworkMediaType = mediaType,
                ArtworkSha256 = actual,
                Artwork = artwork.ToArray(),
            };
        }

        return RelayMapSurfaceResult.Ok;
    }

    /// <summary>What an authenticated paired session may read, or null when nothing is published.</summary>
    public RelayMapSurfaceEntry? Read(RelayPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (!RelayAuthorization.Decide(principal, RelayPermission.ReceiveOpaqueFrames, _timeProvider.GetUtcNow()).Allowed ||
            !_registry.IsCurrent(principal))
        {
            return null;
        }

        lock (_gate)
        {
            return _entry;
        }
    }

    private bool CanPublish(RelayPrincipal principal) =>
        principal.Role == DeviceAuthorizationRole.Owner &&
        RelayAuthorization.Decide(principal, RelayPermission.PublishOpaqueFrames, _timeProvider.GetUtcNow()).Allowed &&
        _registry.IsCurrent(principal);
}
