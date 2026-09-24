using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.ViewModels.V2.Setup;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Core.Abstractions;

namespace TarkovCompanion.App.Services;

/// <summary>Builds Setup's profile compare (#269), kept out of <see cref="AppComposition"/>.</summary>
internal static class ProfileCompareComposition
{
    /// <summary>Null when progress is not stored per profile, where there is nothing to compare.</summary>
    public static SetupProfileCompareViewModel? Create(IServiceProvider provider)
    {
        if (provider.GetRequiredService<IPlayerProfileService>() is not IProfileProgressReader reader)
        {
            return null;
        }

        var service = new ProfileCompareService(
            reader,
            provider.GetRequiredService<IQuestProgressStore>(),
            provider.GetRequiredService<IRaidHistoryService>(),
            provider.GetRequiredService<IItemFactCatalog>());
        return new SetupProfileCompareViewModel(service.CompareAsync);
    }
}
