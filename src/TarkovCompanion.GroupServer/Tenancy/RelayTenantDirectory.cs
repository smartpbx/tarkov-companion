using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.GroupServer.Security;
using TarkovCompanion.GroupServer.StateSync;
using TarkovCompanion.GroupServer.Storage;

namespace TarkovCompanion.GroupServer.Tenancy;

/// <summary>One desktop on this relay, and everything the relay holds for it and its tablets.</summary>
/// <remarks>
/// [#553] Each of these four was written for a relay with exactly one owner, and each still is
/// exactly that: a tenant is a whole single-owner relay of its own. A tablet's session exists in
/// one tenant's registry and nowhere else, so the hub it publishes into, the map it reads and the
/// resume tickets its desktop is shown are that tenant's by construction rather than by a filter
/// somebody has to remember to apply.
/// </remarks>
public sealed class RelayTenant
{
    internal RelayTenant(
        RelayDeviceRegistry registry,
        OpaqueRelayFrameHub? hub,
        RelayMapSurfaceStore? maps,
        RelayResumeTickets tickets,
        bool isLegacy,
        string? room,
        string? storePath)
    {
        Registry = registry;
        Hub = hub;
        Maps = maps;
        Tickets = tickets;
        IsLegacy = isLegacy;
        Room = room;
        StorePath = storePath;
    }

    public RelayDeviceRegistry Registry { get; }

    public OpaqueRelayFrameHub? Hub { get; }

    public RelayMapSurfaceStore? Maps { get; }

    public RelayResumeTickets Tickets { get; }

    /// <summary>
    /// The registry an older desktop claimed with the operator's admin key, in the file this
    /// relay has always kept. It is a tenant like any other once its desktop registers.
    /// </summary>
    public bool IsLegacy { get; }

    /// <summary>The room this desktop last registered from; null until it has (a legacy owner).</summary>
    public string? Room { get; internal set; }

    internal string? StorePath { get; }

    /// <summary>The desktop this tenant belongs to, or null for a legacy registry nobody claimed.</summary>
    public DeviceKeyId? DesktopKeyId => Registry.RecordedOwner()?.DeviceKey.KeyId;

    /// <summary>
    /// Whether <paramref name="named"/> is this desktop, in either of the two ids its one key has:
    /// the device key id the relay records, or the identity key id a tablet pinned when it paired.
    /// </summary>
    public bool IsDesktop(DeviceKeyId named) =>
        Registry.RecordedOwner() is { } owner &&
        (owner.DeviceKey.KeyId == named || PairingCryptography.DesktopIdentityKeyIdOf(owner.DeviceKey) == named);
}

/// <summary>
/// [#553] Every desktop this relay serves. A desktop joins by proving its own key while holding a
/// group key this relay accepts; nobody claims the relay, and the operator's admin key is no part
/// of it.
/// </summary>
/// <remarks>
/// The relay used to have one owner. That made it one person's server: nobody could pair a tablet
/// until somebody had claimed it, only that one desktop could ever have tablets, and a squadmate
/// needed the operator's admin key to be told so. A relay is a group's.
///
/// Storage is one file per desktop, each through its own <see cref="VerifiedRelayRegistryStore"/>,
/// and the file this relay has always written (<c>relay-devices.json</c>) is left exactly as it
/// is: it IS the legacy tenant. So a relay that was claimed before this existed comes up with its
/// owner and that owner's tablets intact, nothing re-paired, and an older relay binary can still
/// read its own file if this one is rolled back. Which room that owner belongs to was never
/// written down; it is learned the first time its desktop registers, which carries a group key.
/// </remarks>
public sealed class RelayTenantDirectory
{
    /// <summary>A squad is five and a group of friends is a few squads.</summary>
    public const int MaximumDesktopsPerRoom = 16;

    /// <summary>
    /// Every tenant may hold a map picture (<see cref="RelayMapSurfaceStore.MaximumArtworkBytes"/>),
    /// so this is also what bounds this relay's memory on an open relay where any key is a room.
    /// </summary>
    public const int MaximumDesktops = 48;

    private const string IndexFileName = "index.json";
    private static readonly JsonSerializerOptions IndexJson = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _timeProvider;
    private readonly OwnerRecoveryProtector _recovery;
    private readonly string? _directory;
    private readonly bool _withHub;
    private readonly bool _withMaps;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ImmutableArray<RelayTenant> _tenants;

    private RelayTenantDirectory(
        TimeProvider timeProvider,
        OwnerRecoveryProtector recovery,
        string? directory,
        RelayTenant legacy,
        ImmutableArray<RelayTenant> tenants)
    {
        _timeProvider = timeProvider;
        _recovery = recovery;
        _directory = directory;
        _withHub = legacy.Hub is not null;
        _withMaps = legacy.Maps is not null;
        Legacy = legacy;
        _tenants = tenants;
    }

    /// <summary>The registry the admin-key claim route and the keyless owner resume still act on.</summary>
    public RelayTenant Legacy { get; }

    /// <summary>Registered desktops first and the legacy registry last; see <see cref="FindBySession"/>.</summary>
    public ImmutableArray<RelayTenant> Tenants => _tenants;

    /// <summary>Tablet map reads being held across every desktop, for <c>/health</c>.</summary>
    public int WaitingCount => _tenants.Sum(tenant => tenant.Maps?.WaitingCount ?? 0);

    /// <param name="legacyRegistry">The single-owner registry this relay had before tenancy.</param>
    /// <param name="directory">Where registered desktops are kept, or null to keep them in memory.</param>
    public static async ValueTask<RelayTenantDirectory> OpenAsync(
        TimeProvider timeProvider,
        OwnerRecoveryProtector recovery,
        RelayDeviceRegistry legacyRegistry,
        OpaqueRelayFrameHub? legacyHub,
        RelayMapSurfaceStore? legacyMaps,
        string? directory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(recovery);
        ArgumentNullException.ThrowIfNull(legacyRegistry);
        directory = directory is null ? null : Path.GetFullPath(directory);
        var rooms = ReadIndex(directory);
        var legacy = new RelayTenant(
            legacyRegistry,
            legacyHub,
            legacyMaps,
            new RelayResumeTickets(timeProvider),
            isLegacy: true,
            room: null,
            storePath: null);
        if (legacy.DesktopKeyId is { } legacyKey && rooms.TryGetValue(legacyKey.Value, out var legacyRoom))
        {
            legacy.Room = legacyRoom;
        }

        var loaded = ImmutableArray.CreateBuilder<RelayTenant>();
        if (directory is not null && Directory.Exists(directory))
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
            {
                if (loaded.Count >= MaximumDesktops ||
                    string.Equals(Path.GetFileName(path), IndexFileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var registry = await RelayDeviceRegistry.OpenAsync(
                    timeProvider,
                    recovery,
                    new VerifiedRelayRegistryStore(path),
                    cancellationToken).ConfigureAwait(false);
                // A file that does not verify names nobody. It is left where it is: the desktop it
                // belonged to lands on the same path when it registers again, and starts over.
                if (registry.RecordedOwner() is not { } owner ||
                    !string.Equals(path, PathFor(directory, owner.DeviceKey.KeyId), StringComparison.Ordinal))
                {
                    continue;
                }

                loaded.Add(new RelayTenant(
                    registry,
                    legacyHub is null ? null : new OpaqueRelayFrameHub(registry, timeProvider),
                    legacyMaps is null ? null : new RelayMapSurfaceStore(registry, timeProvider),
                    new RelayResumeTickets(timeProvider),
                    isLegacy: false,
                    rooms.GetValueOrDefault(owner.DeviceKey.KeyId.Value),
                    path));
            }
        }

        loaded.Add(legacy);
        return new RelayTenantDirectory(timeProvider, recovery, directory, legacy, loaded.ToImmutable());
    }

    /// <summary>The tenant a session belongs to, whether or not that session is still good.</summary>
    /// <remarks>
    /// Registered desktops are searched before the legacy registry. Session ids are unique across
    /// tenants because <see cref="RegisterDesktopAsync"/> and <see cref="AddPairedDeviceAsync"/>
    /// refuse a duplicate, but the legacy claim route is reached with the operator's admin key and
    /// not through here; searched last, nothing claimed there can stand in front of a desktop's
    /// own session.
    /// </remarks>
    public RelayTenant? FindBySession(DeviceSessionId sessionId)
    {
        foreach (var tenant in _tenants)
        {
            if (tenant.Registry.HoldsSession(sessionId))
            {
                return tenant;
            }
        }

        return null;
    }

    /// <summary>
    /// Every tenant that has a paired (never owner) device on record under a key. One browser has
    /// one key, and a household tablet paired first to one desktop and later to another is on
    /// record with both; <paramref name="desktopKeyId"/> is how it says which one it means.
    /// </summary>
    public IReadOnlyList<(RelayTenant Tenant, RelayDeviceRecord Device)> FindPairedDevices(
        DeviceKeyId keyId,
        DeviceKeyId? desktopKeyId = null)
    {
        var found = new List<(RelayTenant, RelayDeviceRecord)>();
        foreach (var tenant in _tenants)
        {
            if (desktopKeyId is { } wanted && !tenant.IsDesktop(wanted))
            {
                continue;
            }

            if (tenant.Registry.FindPairedDeviceByKey(keyId) is { } device)
            {
                found.Add((tenant, device));
            }
        }

        return found;
    }

    /// <summary>
    /// A desktop registering itself, or coming back: the caller has already accepted its group
    /// key for <paramref name="room"/> and spent the nonce <paramref name="completedPairing"/> was
    /// built around, so the signature inside it is proof of the key it names.
    /// </summary>
    /// <remarks>
    /// Idempotent per key. A key this relay knows — a registered desktop, or the legacy owner —
    /// resumes that tenant exactly as <see cref="RelayDeviceRegistry.ResumeOwnerByKeyAsync"/>
    /// always has, tablets intact, and the room it arrived with becomes its room (that is how the
    /// legacy owner's room is learned, and how a desktop that changes groups moves). A key this
    /// relay has never seen gets a registry of its own.
    /// </remarks>
    public async ValueTask<RelayMutationResult<RelaySessionCredential>> RegisterDesktopAsync(
        string room,
        PairingAttempt completedPairing,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(room);
        ArgumentNullException.ThrowIfNull(completedPairing);
        if (completedPairing.Request is not { } request || completedPairing.Establishment is not { } establishment)
        {
            return RelayMutationResult<RelaySessionCredential>.Reject("claim-not-completed");
        }

        var keyId = request.DeviceKey.KeyId;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var tenants = _tenants;
            var existing = tenants.FirstOrDefault(tenant => tenant.DesktopKeyId == keyId);
            // The same desktop on record twice (it registered, and an older build of it then
            // claimed the legacy registry) is one desktop, not a collision with itself.
            if (tenants.Any(tenant => tenant != existing && tenant.DesktopKeyId != keyId &&
                    tenant.Registry.HoldsIdentityOf(establishment)))
            {
                return RelayMutationResult<RelaySessionCredential>.Reject("claim-collision");
            }

            if (existing is not null)
            {
                if (!string.Equals(existing.Room, room, StringComparison.Ordinal) && !HasSpaceIn(room, tenants, existing))
                {
                    return RelayMutationResult<RelaySessionCredential>.Reject("room-full");
                }

                var resumed = await existing.Registry
                    .ResumeOwnerByKeyAsync(completedPairing, CompanionSurfaceKind.Desktop, cancellationToken)
                    .ConfigureAwait(false);
                if (resumed.Succeeded && !string.Equals(existing.Room, room, StringComparison.Ordinal))
                {
                    existing.Room = room;
                    WriteIndex();
                }

                return resumed;
            }

            if (!HasSpaceIn(room, tenants, except: null))
            {
                return RelayMutationResult<RelaySessionCredential>.Reject("room-full");
            }

            // The legacy registry is always in the list and is not one of the registered desktops.
            if (tenants.Length - 1 >= MaximumDesktops)
            {
                tenants = EvictOneForgotten(tenants);
                if (tenants.Length - 1 >= MaximumDesktops)
                {
                    return RelayMutationResult<RelaySessionCredential>.Reject("relay-full");
                }
            }

            var path = _directory is null ? null : PathFor(_directory, keyId);
            var registry = await RelayDeviceRegistry.OpenAsync(
                _timeProvider,
                _recovery,
                path is null ? null : new VerifiedRelayRegistryStore(path),
                cancellationToken).ConfigureAwait(false);
            var registered = await registry
                .RegisterFirstOwnerAsync(completedPairing, CompanionSurfaceKind.Desktop, cancellationToken)
                .ConfigureAwait(false);
            if (!registered.Succeeded)
            {
                return registered;
            }

            var tenant = new RelayTenant(
                registry,
                _withHub ? new OpaqueRelayFrameHub(registry, _timeProvider) : null,
                _withMaps ? new RelayMapSurfaceStore(registry, _timeProvider) : null,
                new RelayResumeTickets(_timeProvider),
                isLegacy: false,
                room,
                path);
            // Before the legacy registry, which stays last.
            _tenants = tenants.Insert(tenants.Length - 1, tenant);
            WriteIndex();
            return registered;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// <see cref="RelayDeviceRegistry.AddPairedDeviceAsync"/> for one tenant, refused when the
    /// pairing names a device, session or channel another desktop already has.
    /// </summary>
    public async ValueTask<RelayMutationResult<RelaySessionCredential>> AddPairedDeviceAsync(
        RelayTenant tenant,
        RelayPrincipal owner,
        PairingAttempt completedPairing,
        DeviceAuthorizationRole role,
        CompanionSurfaceKind surface,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(completedPairing);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A tablet pins the identity key that signed its pairing, so a pairing belongs to the
            // desktop whose key that is. The legacy registry predates the check and an older
            // desktop's tests register pairings signed by a stand-in; a registered desktop's
            // registry takes only what that desktop signed.
            if (!tenant.IsLegacy &&
                (tenant.Registry.RecordedOwner() is not { } desktop ||
                 !PairingCryptography.IsSameKey(desktop.DeviceKey, completedPairing.Offer.DesktopIdentityKey)))
            {
                return RelayMutationResult<RelaySessionCredential>.Reject("pairing-not-this-desktop");
            }

            if (completedPairing.Establishment is { } establishment &&
                _tenants.Any(other => other != tenant && other.Registry.HoldsIdentityOf(establishment)))
            {
                return RelayMutationResult<RelaySessionCredential>.Reject("pairing-collision");
            }

            return await tenant.Registry
                .AddPairedDeviceAsync(owner, completedPairing, role, surface, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool HasSpaceIn(string room, ImmutableArray<RelayTenant> tenants, RelayTenant? except) =>
        tenants.Count(tenant => tenant != except && string.Equals(tenant.Room, room, StringComparison.Ordinal)) <
        MaximumDesktopsPerRoom;

    /// <summary>
    /// Makes room by forgetting the registered desktop that has been gone longest, and only one
    /// whose device record has run out: thirty days unheard. Its tablets went unheard with it.
    /// </summary>
    private ImmutableArray<RelayTenant> EvictOneForgotten(ImmutableArray<RelayTenant> tenants)
    {
        var now = _timeProvider.GetUtcNow();
        var forgotten = tenants
            .Where(tenant => !tenant.IsLegacy && tenant.Registry.RecordedOwner() is { } owner && owner.ExpiresUtc <= now)
            .OrderBy(tenant => tenant.Registry.RecordedOwner()!.LastUsedUtc)
            .FirstOrDefault();
        if (forgotten is null)
        {
            return tenants;
        }

        if (forgotten.StorePath is { } path)
        {
            TryDelete(path);
            TryDelete(path + ".backup");
        }

        _tenants = tenants.Remove(forgotten);
        return _tenants;
    }

    private static string PathFor(string directory, DeviceKeyId keyId) =>
        // Hashed again so the name is lower-case hex whatever a key id looks like: two ids that
        // differ only by case must not be one file on a file system that folds case.
        Path.Combine(
            Path.GetFullPath(directory),
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(keyId.Value)))[..40] + ".json");

    /// <summary>
    /// Key id to room. Not an authorization record: a room here admits nobody, since every
    /// registration is checked against the group key it arrives with. Losing this file costs the
    /// per-room count until each desktop next registers.
    /// </summary>
    private static Dictionary<string, string> ReadIndex(string? directory)
    {
        try
        {
            if (directory is not null && File.Exists(Path.Combine(directory, IndexFileName)))
            {
                var info = new FileInfo(Path.Combine(directory, IndexFileName));
                if (info.Length <= 64 * 1024)
                {
                    return JsonSerializer.Deserialize<Dictionary<string, string>>(
                        File.ReadAllBytes(info.FullName),
                        IndexJson) ?? [];
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        return [];
    }

    private void WriteIndex()
    {
        if (_directory is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_directory);
            var rooms = _tenants
                .Where(tenant => tenant.Room is not null && tenant.DesktopKeyId is not null)
                .GroupBy(tenant => tenant.DesktopKeyId!.Value.Value, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Room!, StringComparer.Ordinal);
            var path = Path.Combine(_directory, IndexFileName);
            var temporary = path + ".writing-" + Guid.NewGuid().ToString("N");
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(rooms, IndexJson));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // See ReadIndex: nothing is authorized by this file.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
