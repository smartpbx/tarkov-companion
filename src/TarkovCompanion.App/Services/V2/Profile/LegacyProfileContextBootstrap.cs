using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Profiles;

namespace TarkovCompanion.App.Services.V2.Profile;

/// <summary>
/// Seeds one v2 <see cref="ProfileContextService"/> profile from the existing v1
/// <see cref="IPlayerProfileService"/> profile, the first time the V2 shell runs.
/// </summary>
/// <remarks>
/// #269/#270 built the v2 profile domain and store, but nothing writes to it: v1's single profile
/// and v2's profile workspace are two disconnected stores today, so
/// <see cref="ProfileRuntimeContextService"/> has no active profile to report on a real launch.
/// This is the smallest fix rather than a real migration: it runs once (skipped the moment any v2
/// profile exists, migrated or user-created) and leaves fields v1 never tracked — wipe season,
/// locale, data snapshot — as honest placeholders rather than guesses. A full migration UI/flow
/// remains #269/#270's scope.
/// </remarks>
public sealed class LegacyProfileContextBootstrap(
    IPlayerProfileService legacyProfiles,
    ProfileContextService profiles,
    RuntimeOptions runtimeOptions,
    // Optional so every test that builds this by hand keeps compiling; the desktop composition
    // always supplies the real coordinator.
    ApplicationStartupCoordinator? startupCoordinator = null,
    ILogger<LegacyProfileContextBootstrap>? logger = null)
{
    private readonly ILogger<LegacyProfileContextBootstrap> _logger = logger ?? NullLogger<LegacyProfileContextBootstrap>.Instance;

    public async Task EnsureSeededAsync(CancellationToken cancellationToken)
    {
        try
        {
            // App.axaml.cs fires this before MainWindowViewModel.InitializeAsync has run the
            // database feature, so this used to query profile_workspaces before its migration
            // had been applied on a fresh data folder. Wait for the same gate every other
            // startup consumer awaits instead.
            if (startupCoordinator is not null)
            {
                await startupCoordinator.DatabaseReadyAsync(cancellationToken).ConfigureAwait(false);
            }

            var workspace = await profiles.GetAsync(cancellationToken).ConfigureAwait(false);
            if (workspace.Profiles.Count > 0)
            {
                return;
            }

            var legacy = await legacyProfiles.GetActiveAsync(cancellationToken).ConfigureAwait(false);
            var generation = string.IsNullOrWhiteSpace(legacy.ProfileGeneration)
                ? $"legacy-{legacy.Id:N}"
                : legacy.ProfileGeneration.Trim();
            var context = new ProfileContext(
                new ProfileIdentity(legacy.Id, generation),
                MapMode(legacy.GameMode),
                new WipeSeason("legacy"),
                new ProfileLocale(runtimeOptions.Language, "US", "Etc/UTC"),
                new DataSnapshotContext("legacy-profile-bootstrap", legacy.UpdatedUtc));
            var record = LegacyProfileMigration.Migrate(legacy, context);
            await profiles.CreateAsync(
                    new CreateProfileRequest(record.Context, record.Name, record.Progress),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Could not seed a v2 profile context from the legacy profile.");
        }
    }

    private static ProfileGameMode MapMode(GameMode mode) => mode switch
    {
        GameMode.Regular => ProfileGameMode.Pvp,
        GameMode.Pve => ProfileGameMode.Pve,
        GameMode.PvpSeason => ProfileGameMode.Seasonal,
        _ => ProfileGameMode.Unknown,
    };
}
