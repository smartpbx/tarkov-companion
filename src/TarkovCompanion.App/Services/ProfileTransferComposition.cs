using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.ViewModels.V2.Setup;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Core.Abstractions;

namespace TarkovCompanion.App.Services;

/// <summary>
/// Builds Setup's profile export/import (#269) from the stores the rest of the app already uses,
/// kept out of <see cref="AppComposition"/> so its registration stays one line.
/// </summary>
internal static class ProfileTransferComposition
{
    public static SetupProfileTransferViewModel Create(IServiceProvider provider, AppDataPaths paths)
    {
        var management = provider.GetRequiredService<ProfileManagementService>();
        var service = new ProfileBundleService(
            provider.GetRequiredService<IPlayerProfileService>(),
            provider.GetRequiredService<IQuestProgressStore>(),
            provider.GetRequiredService<IRaidHistoryService>(),
            async cancellationToken =>
            {
                var active = (await management.LoadAsync(cancellationToken).ConfigureAwait(false)).ActiveProfile;
                return active is null
                    ? null
                    : new ProfileBundleIdentity(active.Name, active.Context.Mode, active.Context.WipeSeason.Value);
            },
            (identity, cancellationToken) => management.CreateAsync(
                UnusedName(identity.Name, management.Current.Workspace.Profiles.Select(profile => profile.Name)),
                identity.Mode,
                identity.Wipe,
                cancellationToken),
            provider.GetRequiredService<TimeProvider>());
        return new SetupProfileTransferViewModel(
            service,
            Path.Combine(paths.Root, "Exports"),
            provider.GetRequiredService<TimeProvider>());
    }

    /// <summary>
    /// The file's name, or "name (imported)" when a profile is already called that: importing
    /// your own export as a new profile otherwise left two identical rows and no way to tell
    /// which one the switch button meant.
    /// </summary>
    internal static string UnusedName(string name, IEnumerable<string> taken)
    {
        var names = taken.ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        if (!names.Contains(name))
        {
            return name;
        }

        for (var attempt = 1; ; attempt++)
        {
            var candidate = attempt == 1 ? $"{name} (imported)" : $"{name} (imported {attempt})";
            if (!names.Contains(candidate))
            {
                return candidate;
            }
        }
    }
}
