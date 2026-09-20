using TarkovCompanion.Core.Domain.Profiles;

namespace TarkovCompanion.Application.Services.Profiles;

/// <summary>What a player can ask of the profile list, in words they would use.</summary>
/// <remarks>
/// #269 built the create / switch / archive / restore operations and a runtime context that turns
/// the active profile into a catalog scope, and nothing called them. This is the one place the app
/// asks: it turns "a PvE profile called Alt for this wipe" into the immutable context the domain
/// wants, and it is what the Setup page binds, so a test on it is a test of what the player can do.
/// </remarks>
public sealed class ProfileManagementService(
    ProfileContextService profiles,
    IProfileRuntimeContextService runtime,
    TimeProvider? timeProvider = null,
    // The first profile a V2 launch shows is the V1 one, seeded once. Creating a profile before that
    // has happened would leave V1's progress with no profile to belong to, so a create waits for it.
    Func<CancellationToken, Task>? ensureLegacySeeded = null)
{
    public const string DefaultWipe = "Current wipe";

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public ProfileRuntimeContextSnapshot Current => runtime.Current;

    public event Action<ProfileRuntimeContextChanged>? Changed
    {
        add => runtime.ContextChanged += value;
        remove => runtime.ContextChanged -= value;
    }

    public Task<ProfileRuntimeContextSnapshot> LoadAsync(CancellationToken cancellationToken) =>
        runtime.InitializeAsync(cancellationToken);

    /// <summary>Creates a profile with its own empty progress and makes it the active one.</summary>
    /// <exception cref="ArgumentException">The name or wipe is blank, too long or holds control characters.</exception>
    public async Task<ProfileRuntimeContextSnapshot> CreateAsync(
        string name,
        ProfileGameMode mode,
        string wipe,
        CancellationToken cancellationToken)
    {
        if (mode == ProfileGameMode.Unknown)
        {
            // Offering "unknown" would create exactly the profile whose data cannot be loaded.
            throw new ArgumentException("Choose PvP, PvE or Seasonal.", nameof(mode));
        }

        var trimmedName = name?.Trim() ?? string.Empty;
        var trimmedWipe = string.IsNullOrWhiteSpace(wipe) ? DefaultWipe : wipe.Trim();
        var current = await runtime.InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (current.Workspace.Profiles.Count == 0 && ensureLegacySeeded is not null)
        {
            await ensureLegacySeeded(cancellationToken).ConfigureAwait(false);
            current = await runtime.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }

        var now = _time.GetUtcNow();
        // Language, region and time zone are the player's, not the profile's, so a new profile
        // inherits them from the one they are already using; only a first profile falls back.
        var locale = current.ActiveProfile?.Context.Locale ?? new ProfileLocale("en", "US", "Etc/UTC");
        var context = new ProfileContext(
            new ProfileIdentity(Guid.NewGuid(), Guid.NewGuid().ToString("N")),
            mode,
            new WipeSeason(trimmedWipe),
            locale,
            new DataSnapshotContext("profile-created", now));
        await profiles.CreateAsync(
                new CreateProfileRequest(context, trimmedName, new ProfileProgress(level: 1)),
                cancellationToken)
            .ConfigureAwait(false);
        return await runtime.RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<ProfileRuntimeContextSnapshot> SwitchAsync(Guid profileId, CancellationToken cancellationToken) =>
        runtime.SwitchAsync(profileId, cancellationToken);

    public async Task<ProfileRuntimeContextSnapshot> ArchiveAsync(Guid profileId, CancellationToken cancellationToken)
    {
        await profiles.ArchiveAsync(profileId, cancellationToken).ConfigureAwait(false);
        return await runtime.RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProfileRuntimeContextSnapshot> RestoreAsync(Guid profileId, CancellationToken cancellationToken)
    {
        await profiles.RestoreAsync(profileId, cancellationToken).ConfigureAwait(false);
        return await runtime.RefreshAsync(cancellationToken).ConfigureAwait(false);
    }
}
