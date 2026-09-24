using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Profiles;

namespace TarkovCompanion.Infrastructure.Profile;

/// <summary>
/// Gives every profile in the workspace its own progress file, and hands each caller of
/// <see cref="IPlayerProfileService"/> the file that belongs to the profile that is active now.
/// </summary>
/// <remarks>
/// #269 asked that profiles never merge state. The profile workspace (which profile is active, and
/// its mode and wipe) and the progress the app actually reads and writes (quests, hideout, wishlist,
/// owned counts - all through <see cref="IPlayerProfileService"/>) were two unconnected stores, so a
/// switcher built on the workspace alone would have changed the game mode and left one profile's
/// quest progress showing under another. Routing the one interface fixes that for its thirty-odd
/// callers without touching them.
///
/// The profile that was already there keeps <c>profile.json</c>, so nothing moves on upgrade. It is
/// recognised by its stable id, read once from that file. Every other profile gets
/// <c>profiles/{id}.json</c>, created the first time it is read, seeded from the workspace record.
///
/// A write that carries a different profile's id is refused instead of landing in whichever file
/// happens to be active: a quest edit begun before a switch must fail visibly, not overwrite the
/// profile the player has since moved to. With no active profile (a V1 launch, or before the first
/// profile exists) everything goes to <c>profile.json</c> exactly as before.
/// </remarks>
public sealed class ProfileScopedPlayerProfileService : IPlayerProfileService, IPlayerProfileChangeSource, IProfileProgressReader, IDisposable
{
    private readonly IPlayerProfileService _legacy;
    private readonly IProfileRuntimeContextService _context;
    private readonly Func<string, IPlayerProfileService> _openFile;
    private readonly string _profilesDirectory;
    private readonly ConcurrentDictionary<Guid, IPlayerProfileService> _files = new();
    private readonly SemaphoreSlim _seedGate = new(1, 1);
    private readonly ILogger<ProfileScopedPlayerProfileService> _logger;
    private Guid? _legacyProfileId;

    public event Action<PlayerProfile>? Changed;

    public ProfileScopedPlayerProfileService(
        IPlayerProfileService legacy,
        IProfileRuntimeContextService context,
        string profilesDirectory,
        Func<string, IPlayerProfileService> openFile,
        ILogger<ProfileScopedPlayerProfileService>? logger = null)
    {
        _logger = logger ?? NullLogger<ProfileScopedPlayerProfileService>.Instance;
        _legacy = legacy ?? throw new ArgumentNullException(nameof(legacy));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _profilesDirectory = string.IsNullOrWhiteSpace(profilesDirectory)
            ? throw new ArgumentException("A profiles directory is required.", nameof(profilesDirectory))
            : Path.GetFullPath(profilesDirectory);
        _openFile = openFile ?? throw new ArgumentNullException(nameof(openFile));
    }

    /// <summary>The file a profile's progress lives in; exposed so a test and Setup can name it.</summary>
    public string PathFor(Guid profileId) => Path.Combine(_profilesDirectory, $"{profileId:N}.json");

    public async Task<PlayerProfile> GetActiveAsync(CancellationToken cancellationToken)
    {
        var target = await ResolveAsync(cancellationToken).ConfigureAwait(false);
        var profile = await target.Service.GetActiveAsync(cancellationToken).ConfigureAwait(false);
        return Remember(target, profile);
    }

    public async Task SaveAsync(PlayerProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var target = await ResolveAsync(cancellationToken).ConfigureAwait(false);
        if (target.Active is { } active && profile.Id != active.Context.Identity.ProfileId)
        {
            throw new InvalidOperationException(
                "The active profile changed while this progress was being saved; reload it and try again.");
        }

        await target.Service.SaveAsync(profile, cancellationToken).ConfigureAwait(false);
        PublishChanged(profile);
    }

    public async Task<string> ExportJsonAsync(CancellationToken cancellationToken)
    {
        var target = await ResolveAsync(cancellationToken).ConfigureAwait(false);
        return await target.Service.ExportJsonAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<PlayerProfile> ImportJsonAsync(string json, CancellationToken cancellationToken)
    {
        var target = await ResolveAsync(cancellationToken).ConfigureAwait(false);
        var imported = await target.Service.ImportJsonAsync(json, cancellationToken).ConfigureAwait(false);
        if (target.Active is not { } active || imported.Id == active.Context.Identity.ProfileId)
        {
            var remembered = Remember(target, imported);
            PublishChanged(remembered);
            return remembered;
        }

        // An import carries the id of whoever exported it. It replaces this profile's progress; it
        // must not rename the profile the workspace knows.
        var pinned = imported with
        {
            Id = active.Context.Identity.ProfileId,
            ProfileGeneration = active.Context.Identity.Generation,
            GameMode = ToLegacyMode(active.Context.Mode),
        };
        await target.Service.SaveAsync(pinned, cancellationToken).ConfigureAwait(false);
        PublishChanged(pinned);
        return pinned;
    }

    /// <summary>
    /// [#269] Any profile's progress, for Setup's compare, without switching to it and without
    /// writing anything: a profile whose file does not exist yet reads as its workspace record.
    /// </summary>
    public async Task<PlayerProfile> ReadAsync(ProfileRecord profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var id = profile.Context.Identity.ProfileId;
        _legacyProfileId ??= (await _legacy.GetActiveAsync(cancellationToken).ConfigureAwait(false)).Id;
        if (id == _legacyProfileId)
        {
            return await _legacy.GetActiveAsync(cancellationToken).ConfigureAwait(false);
        }

        var path = PathFor(id);
        if (!File.Exists(path))
        {
            return SeedFrom(profile);
        }

        var file = _files.GetOrAdd(id, _ => _openFile(path));
        return await file.GetActiveAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        foreach (var file in _files.Values)
        {
            (file as IDisposable)?.Dispose();
        }

        _seedGate.Dispose();
    }

    private async Task<Target> ResolveAsync(CancellationToken cancellationToken)
    {
        var active = await ActiveProfileAsync(cancellationToken).ConfigureAwait(false);
        if (active is null)
        {
            return new(_legacy, null);
        }

        var id = active.Context.Identity.ProfileId;
        if (_legacyProfileId is null)
        {
            _legacyProfileId = (await _legacy.GetActiveAsync(cancellationToken).ConfigureAwait(false)).Id;
        }

        if (id == _legacyProfileId)
        {
            return new(_legacy, active);
        }

        var path = PathFor(id);
        await _seedGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var file = _files.GetOrAdd(id, _ => _openFile(path));
            if (!File.Exists(path))
            {
                // A profile is created in the workspace first; its progress file starts as what the
                // workspace record says, not as a random default the reader would invent.
                await file.SaveAsync(SeedFrom(active), cancellationToken).ConfigureAwait(false);
            }

            return new(file, active);
        }
        finally
        {
            _seedGate.Release();
        }
    }

    /// <summary>
    /// The active profile, loading the workspace the first time so a V1 launch and a V2 launch agree
    /// on which profile is open. An unreadable workspace (the table is not migrated yet, early in a
    /// launch) means "no profile" for now and is asked again on the next call.
    /// </summary>
    private async Task<ProfileRecord?> ActiveProfileAsync(CancellationToken cancellationToken)
    {
        var snapshot = _context.Current;
        if (!snapshot.IsInitialized)
        {
            try
            {
                snapshot = await _context.InitializeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "The profile workspace could not be read; using the default profile file.");
                return null;
            }
        }

        return snapshot.ActiveProfile;
    }

    private PlayerProfile Remember(Target target, PlayerProfile profile)
    {
        if (ReferenceEquals(target.Service, _legacy))
        {
            _legacyProfileId = profile.Id;
        }

        return profile;
    }

    /// <summary>
    /// The profile is already durable when this runs. A presentation subscriber cannot turn a
    /// successful save into a failed one or prevent another subscriber from hearing about it.
    /// </summary>
    private void PublishChanged(PlayerProfile profile)
    {
        if (Changed is not { } handlers)
        {
            return;
        }

        foreach (Action<PlayerProfile> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(profile);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "A player-profile subscriber failed after profile {ProfileId} was saved.", profile.Id);
            }
        }
    }

    /// <summary>
    /// The legacy file has no "unknown" mode. A profile whose mode is unknown is stored as Regular
    /// there, but nothing reads the mode back from the file to fetch data: the runtime context still
    /// reports it as unknown and the catalog is not synced for it.
    /// </summary>
    private static GameMode ToLegacyMode(ProfileGameMode mode) => mode switch
    {
        ProfileGameMode.Pve => GameMode.Pve,
        ProfileGameMode.Seasonal => GameMode.PvpSeason,
        _ => GameMode.Regular,
    };

    private static PlayerProfile SeedFrom(ProfileRecord record)
    {
        var progress = record.Progress;
        var events = new Dictionary<string, EventItemState>(StringComparer.Ordinal);
        foreach (var (key, value) in progress.EventItemStates)
        {
            if (Enum.TryParse<EventItemState>(value, ignoreCase: true, out var state))
            {
                events[key] = state;
            }
        }

        return new(
            record.Context.Identity.ProfileId,
            record.Name,
            ToLegacyMode(record.Context.Mode),
            progress.Level,
            Faction.Unknown,
            null,
            new Dictionary<string, int>(progress.TraderLevels, StringComparer.Ordinal),
            new HashSet<string>(progress.CompletedTaskIds, StringComparer.Ordinal),
            new Dictionary<string, int>(progress.ObjectiveProgress, StringComparer.Ordinal),
            new Dictionary<string, int>(progress.HideoutStationLevels, StringComparer.Ordinal),
            new HashSet<string>(progress.WishlistItemIds, StringComparer.Ordinal),
            new Dictionary<string, int>(progress.OwnedItemCounts, StringComparer.Ordinal),
            events,
            new Dictionary<string, string>(progress.ItemOverrides, StringComparer.Ordinal),
            record.UpdatedUtc,
            record.Context.Identity.Generation);
    }

    private readonly record struct Target(IPlayerProfileService Service, ProfileRecord? Active);
}
