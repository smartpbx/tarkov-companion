using TarkovCompanion.Core.Domain.Profiles;

namespace TarkovCompanion.Application.Services.Profiles;

/// <summary>Persistence owns compare-and-swap; Application owns profile lifecycle semantics.</summary>
public interface IProfileWorkspaceStore
{
    Task<ProfileWorkspaceSnapshot> ReadAsync(CancellationToken cancellationToken);

    Task<bool> TryReplaceAsync(
        long expectedRevision,
        ProfileWorkspaceSnapshot replacement,
        CancellationToken cancellationToken);
}

public sealed record ProfileContextChanged(ProfileWorkspaceSnapshot Snapshot);

public sealed record CreateProfileRequest(ProfileContext Context, string Name, ProfileProgress Progress, bool MakeActive = true);

public sealed class ProfileContextService(IProfileWorkspaceStore store, TimeProvider? timeProvider = null) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public event Action<ProfileContextChanged>? ContextChanged;

    public Task<ProfileWorkspaceSnapshot> GetAsync(CancellationToken cancellationToken) => store.ReadAsync(cancellationToken);

    public Task<ProfileWorkspaceSnapshot> CreateAsync(CreateProfileRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Context);
        ArgumentNullException.ThrowIfNull(request.Progress);
        return MutateAsync(snapshot =>
        {
            if (snapshot.Profiles.Any(profile => profile.Context.Identity.ProfileId == request.Context.Identity.ProfileId))
            {
                throw new InvalidOperationException("A profile with this stable id already exists; create a new generation only through an explicit rollover.");
            }

            var record = new ProfileRecord(request.Context, request.Name, request.Progress, ProfileLifecycle.Active, UtcNow());
            return new ProfileWorkspaceSnapshot(
                checked(snapshot.Revision + 1),
                request.MakeActive || snapshot.ActiveProfileId is null ? request.Context.Identity.ProfileId : snapshot.ActiveProfileId,
                snapshot.Profiles.Append(record).ToArray());
        }, cancellationToken);
    }

    public Task<ProfileWorkspaceSnapshot> SwitchAsync(Guid profileId, CancellationToken cancellationToken) =>
        MutateAsync(snapshot =>
        {
            var profile = Find(snapshot, profileId);
            if (profile.Lifecycle == ProfileLifecycle.Archived)
            {
                throw new InvalidOperationException("An archived profile must be restored before it can become active.");
            }

            return new ProfileWorkspaceSnapshot(checked(snapshot.Revision + 1), profileId, snapshot.Profiles);
        }, cancellationToken);

    public Task<ProfileWorkspaceSnapshot> ArchiveAsync(Guid profileId, CancellationToken cancellationToken) =>
        MutateAsync(snapshot =>
        {
            if (snapshot.ActiveProfileId == profileId)
            {
                throw new InvalidOperationException("Switch to another profile before archiving the active one.");
            }

            var profile = Find(snapshot, profileId);
            if (profile.Lifecycle == ProfileLifecycle.Archived)
            {
                return snapshot;
            }

            return Replace(snapshot, profileId, ProfileLifecycle.Archived);
        }, cancellationToken);

    public Task<ProfileWorkspaceSnapshot> RestoreAsync(Guid profileId, CancellationToken cancellationToken) =>
        MutateAsync(snapshot =>
        {
            var profile = Find(snapshot, profileId);
            return profile.Lifecycle == ProfileLifecycle.Active ? snapshot : Replace(snapshot, profileId, ProfileLifecycle.Active);
        }, cancellationToken);

    public async Task<ProfileComparison> CompareAsync(Guid leftProfileId, Guid rightProfileId, CancellationToken cancellationToken)
    {
        var snapshot = await store.ReadAsync(cancellationToken).ConfigureAwait(false);
        return ProfileComparison.Create(Find(snapshot, leftProfileId), Find(snapshot, rightProfileId));
    }

    public async Task<ProfileWorkspaceSnapshot> ImportAsync(
        IReadOnlyList<ProfileRecord> readyProfiles,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readyProfiles);
        return await MutateAsync(snapshot =>
        {
            var existing = snapshot.Profiles.Select(profile => profile.Context.Identity.ProfileId).ToHashSet();
            if (readyProfiles.Any(profile => existing.Contains(profile.Context.Identity.ProfileId)) ||
                readyProfiles.Select(profile => profile.Context.Identity.ProfileId).Distinct().Count() != readyProfiles.Count)
            {
                throw new InvalidOperationException("Import attempts to merge profile state; resolve the visible context conflict first.");
            }

            return new ProfileWorkspaceSnapshot(
                checked(snapshot.Revision + 1),
                snapshot.ActiveProfileId,
                snapshot.Profiles.Concat(readyProfiles).ToArray());
        }, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _gate.Dispose();

    private async Task<ProfileWorkspaceSnapshot> MutateAsync(
        Func<ProfileWorkspaceSnapshot, ProfileWorkspaceSnapshot> mutate,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = await store.ReadAsync(cancellationToken).ConfigureAwait(false);
                var replacement = mutate(current);
                if (ReferenceEquals(replacement, current))
                {
                    return current;
                }

                if (!await store.TryReplaceAsync(current.Revision, replacement, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                ContextChanged?.Invoke(new(replacement));
                return replacement;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private ProfileWorkspaceSnapshot Replace(ProfileWorkspaceSnapshot snapshot, Guid profileId, ProfileLifecycle lifecycle) => new(
        checked(snapshot.Revision + 1),
        snapshot.ActiveProfileId,
        snapshot.Profiles.Select(profile => profile.Context.Identity.ProfileId == profileId
            ? new ProfileRecord(profile.Context, profile.Name, profile.Progress, lifecycle, UtcNow())
            : profile).ToArray());

    private static ProfileRecord Find(ProfileWorkspaceSnapshot snapshot, Guid profileId) =>
        snapshot.Profiles.SingleOrDefault(profile => profile.Context.Identity.ProfileId == profileId)
        ?? throw new KeyNotFoundException($"Profile '{profileId:D}' does not exist.");

    private DateTimeOffset UtcNow() => _timeProvider.GetUtcNow().ToUniversalTime();
}

public sealed record ProfileComparison(
    ProfileIdentity Left,
    ProfileIdentity Right,
    bool SameMode,
    bool SameWipeSeason,
    bool SameLocale,
    bool SameDataSnapshot,
    int LeftCompletedTasks,
    int RightCompletedTasks,
    int LeftWishlistItems,
    int RightWishlistItems)
{
    public static ProfileComparison Create(ProfileRecord left, ProfileRecord right) => new(
        left.Context.Identity,
        right.Context.Identity,
        left.Context.Mode == right.Context.Mode,
        string.Equals(left.Context.WipeSeason.Value, right.Context.WipeSeason.Value, StringComparison.Ordinal),
        Equals(left.Context.Locale, right.Context.Locale),
        Equals(left.Context.DataSnapshot, right.Context.DataSnapshot),
        left.Progress.CompletedTaskIds.Count,
        right.Progress.CompletedTaskIds.Count,
        left.Progress.WishlistItemIds.Count,
        right.Progress.WishlistItemIds.Count);
}
