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
    DateTimeOffset PublishedUtc,
    long Revision);

/// <summary>Whether the relay holds the owner's map, and which picture goes with it.</summary>
public sealed record RelayHeldMap(bool Held, long Revision, string? ArtworkSha256);

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

    /// <summary>The longest this relay will hold a map read, whatever the caller asked for.</summary>
    /// <remarks>
    /// The same twenty seconds <c>GroupRoomChanges.MaximumWait</c> uses for the group exchange, and
    /// deliberately the same number: both are held requests on the same Kestrel, well inside its
    /// 130-second keep-alive, so a held read is never the thing that times out. If one moves the
    /// other has to.
    /// </remarks>
    public static readonly TimeSpan MaximumWait = TimeSpan.FromSeconds(20);

    /// <summary>How many map reads may be held at once.</summary>
    /// <remarks>
    /// A held read costs a socket and a continuation, not a thread. Past the bound a caller is
    /// answered immediately rather than refused: the read still works, it is just as slow as it
    /// was before the hold existed.
    /// </remarks>
    public const int MaximumConcurrentWaits = 256;

    private readonly RelayDeviceRegistry _registry;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();
    private RelayMapSurfaceEntry? _entry;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _revision;
    private int _waiting;

    /// <summary>How many map reads are being held right now, for <c>/health</c> to report.</summary>
    public int WaitingCount => Volatile.Read(ref _waiting);

    /// <summary>The revision a caller compares against, for a relay that has published nothing yet.</summary>
    public long Revision
    {
        get
        {
            lock (_gate)
            {
                return _revision;
            }
        }
    }

    public RelayMapSurfaceStore(RelayDeviceRegistry registry, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _registry = registry;
        _timeProvider = timeProvider;
    }

    /// <summary>The clock, cut to whole milliseconds, which is what relay authorization accepts.</summary>
    /// <remarks>
    /// <see cref="RelayAuthorization.Decide"/> refuses a timestamp with sub-millisecond ticks. This
    /// store handed it the raw clock, and every test drove it with a manual clock set to whole
    /// milliseconds, so the suite was green while on the real relay every map publish and every
    /// map read threw and answered 500: a paired tablet said "the desktop is offline" and never
    /// drew a map (2026-09-20, the first night anybody paired one for real).
    /// </remarks>
    private DateTimeOffset Now()
    {
        var utc = _timeProvider.GetUtcNow().ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);
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

        TaskCompletionSource woken;
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
                Now(),
                ++_revision);
            woken = Swap();
        }

        // Outside the lock: a woken read reads this store back, and waking it while holding the
        // lock would have it wait on the thread that is about to release it.
        woken.TrySetResult();
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

        TaskCompletionSource woken;
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
                Revision = ++_revision,
            };
            woken = Swap();
        }

        woken.TrySetResult();
        return RelayMapSurfaceResult.Ok;
    }

    /// <summary>
    /// What this relay holds for its owner, as the owner is told on every read of its queue: the
    /// revision, and the hash of the picture if it has one. Null for anybody but that owner.
    /// </summary>
    /// <remarks>
    /// [#407] This store is memory-only, so a relay restart empties it, and the desktop only
    /// uploads when something changed. Without being told, a desktop sitting on one map never
    /// learned its tablets had been looking at nothing since the restart.
    /// </remarks>
    public RelayHeldMap? Describe(RelayPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (!CanPublish(principal))
        {
            return null;
        }

        lock (_gate)
        {
            return _entry is { } entry && entry.OwnerDeviceId == principal.DeviceId
                ? new RelayHeldMap(true, entry.Revision, entry.ArtworkSha256)
                : new RelayHeldMap(false, 0, null);
        }
    }

    /// <summary>What an authenticated paired session may read, or null when nothing is published.</summary>
    public RelayMapSurfaceEntry? Read(RelayPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (!CanRead(principal))
        {
            return null;
        }

        lock (_gate)
        {
            return _entry;
        }
    }

    private bool CanRead(RelayPrincipal principal) =>
        RelayAuthorization.Decide(principal, RelayPermission.ReceiveOpaqueFrames, Now()).Allowed &&
        _registry.IsCurrent(principal);

    /// <summary>
    /// Waits until the desktop publishes something newer than the caller already has.
    /// </summary>
    /// <remarks>
    /// [V2 rough package 34] The same hold #422 gave the group exchange, for the same reason and
    /// with the same rule: a caller that names both a revision and a wait is held until the map
    /// moves past that revision or the wait runs out; a caller that names neither is answered
    /// exactly as it always was. That is what lets an older tablet page and an older relay each
    /// keep working against a newer counterpart.
    ///
    /// Before this the tablet polled on its own timer while the desktop published on a timer of
    /// its own, so the second screen was two waits behind the desk for a change that costs a few
    /// hundred bytes to carry.
    /// </remarks>
    public async Task<RelayMapSurfaceEntry?> WaitAsync(
        RelayPrincipal principal,
        long since,
        TimeSpan wait,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        // A device that may not read is answered now, not in twenty seconds: "nothing published
        // yet" is worth waiting for and "you are revoked" is not.
        if (!CanRead(principal))
        {
            return null;
        }

        var current = Read(principal);
        if (wait <= TimeSpan.Zero || (current?.Revision ?? 0) > since)
        {
            return current;
        }

        if (wait > MaximumWait)
        {
            wait = MaximumWait;
        }

        if (Interlocked.Increment(ref _waiting) > MaximumConcurrentWaits)
        {
            // Over the bound the read degrades to what it was before the hold existed.
            Interlocked.Decrement(ref _waiting);
            return current;
        }

        // One source for both ends: the caller hanging up and the hold expiring are the same
        // outcome, and neither may leave a timer running behind the answer.
        using var expiry = new CancellationTokenSource(wait, _timeProvider);
        using var hold = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, expiry.Token);
        try
        {
            while (true)
            {
                // The signal is taken before the entry is read, so a publish landing between the
                // two is waited on rather than missed.
                Task changed;
                lock (_gate)
                {
                    changed = _changed.Task;
                }

                // Re-authorised on every turn rather than once: a device revoked while its read is
                // held must not be answered with the map when the hold ends.
                if (!CanRead(principal))
                {
                    return null;
                }

                current = Read(principal);
                if (current is { } published && published.Revision > since)
                {
                    return published;
                }

                if (hold.IsCancellationRequested)
                {
                    // A tablet that paired before the desktop published anything waits here for
                    // the first publish rather than spinning on 404s.
                    return current;
                }

                await changed.WaitAsync(hold.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The hold expired, or the caller hung up. Either way the map is answered as it
            // stands, which is what a caller that never asked to wait would have been given.
            return Read(principal);
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }
    }

    private TaskCompletionSource Swap()
    {
        var woken = _changed;
        _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        return woken;
    }

    private bool CanPublish(RelayPrincipal principal) =>
        principal.Role == DeviceAuthorizationRole.Owner &&
        RelayAuthorization.Decide(principal, RelayPermission.PublishOpaqueFrames, Now()).Allowed &&
        _registry.IsCurrent(principal);
}
