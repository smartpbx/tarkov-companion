using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

/// <summary>
/// Mutations are serialized and committed by compare-and-swap. <see cref="ContextChanged"/> is
/// raised only after the mutation gate is released, one change at a time and in commit order.
/// Raising it inside the gate let a subscriber that reacted by switching profile wait on the gate
/// its own notification was holding, and let a throwing subscriber turn a revision that the store
/// had already committed into a failed call. A reentrant mutation therefore returns before its own
/// notification is delivered; that notification follows the one being delivered, never precedes it.
/// </summary>
public sealed class ProfileContextService : IDisposable
{
    private readonly IProfileWorkspaceStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentQueue<ProfileContextChanged> _pendingChanges = new();
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ProfileContextService> _logger;
    private int _publishingChanges;

    public ProfileContextService(
        IProfileWorkspaceStore store,
        TimeProvider? timeProvider = null,
        ILogger<ProfileContextService>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<ProfileContextService>.Instance;
    }

    public event Action<ProfileContextChanged>? ContextChanged;

    public Task<ProfileWorkspaceSnapshot> GetAsync(CancellationToken cancellationToken) => ReadAsync(cancellationToken);

    public Task<ProfileWorkspaceSnapshot> CreateAsync(CreateProfileRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Context);
        ArgumentNullException.ThrowIfNull(request.Progress);
        return MutateAsync(snapshot =>
        {
            if (snapshot.Profiles.Any(profile => profile.Context.Identity.ProfileId == request.Context.Identity.ProfileId))
            {
                throw new InvalidOperationException("A profile with this stable id already exists; a separate generation requires a distinct stable id.");
            }

            EnsureCapacity(snapshot, 1);
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

            // Selecting the profile that is already active changes nothing, so it must not mint a
            // revision or a notification that every subscriber would re-render for.
            return snapshot.ActiveProfileId == profileId
                ? snapshot
                : new ProfileWorkspaceSnapshot(checked(snapshot.Revision + 1), profileId, snapshot.Profiles);
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
        var snapshot = await ReadAsync(cancellationToken).ConfigureAwait(false);
        return ProfileComparison.Create(Find(snapshot, leftProfileId), Find(snapshot, rightProfileId));
    }

    public async Task<ProfileWorkspaceSnapshot> ImportAsync(
        IReadOnlyList<ProfileRecord> readyProfiles,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readyProfiles);

        // Frozen before the gate wait: the caller's list cannot change what is imported.
        var frozenProfiles = new ProfileWorkspaceSnapshot(0, null, readyProfiles).Profiles;
        return await MutateAsync(snapshot =>
        {
            // Nothing to add is not a change; it must not publish a revision that says otherwise.
            if (frozenProfiles.Count == 0)
            {
                return snapshot;
            }

            var existing = snapshot.Profiles.Select(profile => profile.Context.Identity.ProfileId).ToHashSet();
            if (frozenProfiles.Any(profile => existing.Contains(profile.Context.Identity.ProfileId)))
            {
                throw new InvalidOperationException("Import attempts to merge profile state; resolve the visible context conflict first.");
            }

            EnsureCapacity(snapshot, frozenProfiles.Count);
            return new ProfileWorkspaceSnapshot(
                checked(snapshot.Revision + 1),
                snapshot.ActiveProfileId,
                snapshot.Profiles.Concat(frozenProfiles).ToArray());
        }, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _gate.Dispose();

    private async Task<ProfileWorkspaceSnapshot> MutateAsync(
        Func<ProfileWorkspaceSnapshot, ProfileWorkspaceSnapshot> mutate,
        CancellationToken cancellationToken)
    {
        ProfileWorkspaceSnapshot result;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = await ReadAsync(cancellationToken).ConfigureAwait(false);
                var replacement = mutate(current);
                if (ReferenceEquals(replacement, current))
                {
                    result = current;
                    break;
                }

                if (!await _store.TryReplaceAsync(current.Revision, replacement, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                // Queued while the gate is still held, so queue order is commit order.
                _pendingChanges.Enqueue(new(replacement));
                result = replacement;
                break;
            }
        }
        finally
        {
            _gate.Release();
        }

        PublishPendingChanges();
        return result;
    }

    private async Task<ProfileWorkspaceSnapshot> ReadAsync(CancellationToken cancellationToken) =>
        await _store.ReadAsync(cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidOperationException("The profile workspace store returned no snapshot.");

    private void PublishPendingChanges()
    {
        // One publisher drains at a time. A caller that loses the flag leaves its change to the
        // publisher holding it; the emptiness check after the flag is cleared catches a change
        // queued in the instant between that publisher's last dequeue and its release.
        while (Interlocked.CompareExchange(ref _publishingChanges, 1, 0) == 0)
        {
            try
            {
                while (_pendingChanges.TryDequeue(out var change))
                {
                    Deliver(change);
                }
            }
            finally
            {
                Volatile.Write(ref _publishingChanges, 0);
            }

            if (_pendingChanges.IsEmpty)
            {
                return;
            }
        }
    }

    private void Deliver(ProfileContextChanged change)
    {
        if (ContextChanged is not { } handlers)
        {
            return;
        }

        foreach (Action<ProfileContextChanged> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(change);
            }
            catch (Exception exception)
            {
                // The revision is already committed. Reporting it as a failed switch would invite a
                // retry of something that succeeded, and skipping the remaining subscribers would
                // leave them showing the previous profile.
                _logger.LogWarning(
                    exception,
                    "A profile context subscriber failed after revision {Revision} was committed.",
                    change.Snapshot.Revision);
            }
        }
    }

    private static void EnsureCapacity(ProfileWorkspaceSnapshot snapshot, int adding)
    {
        if (snapshot.Profiles.Count + adding > ProfileWorkspaceSnapshot.MaximumProfiles)
        {
            throw new InvalidOperationException($"A workspace contains at most {ProfileWorkspaceSnapshot.MaximumProfiles} profiles.");
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
