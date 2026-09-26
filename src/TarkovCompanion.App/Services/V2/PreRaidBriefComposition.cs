using System;
using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Situations;
using TarkovCompanion.Infrastructure.Persistence;
using TarkovCompanion.Infrastructure.Persistence.Repositories;

namespace TarkovCompanion.App.Services.V2;

/// <summary>[#712 0-9] The pre-raid brief's catalog read and its hook into the Raid panel.</summary>
internal static class PreRaidBriefComposition
{
    public static void Add(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IMapBossCatalog>(provider =>
            new SqliteMapBossCatalog(provider.GetRequiredService<SqliteConnectionFactory>()));
    }

    /// <summary>Called as the Raid cockpit is built, so every composition that has one has the brief.</summary>
    public static RaidCockpitViewModel Attach(IServiceProvider provider, RaidCockpitViewModel cockpit)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (provider.GetService<SituationService>() is { } situation)
        {
            cockpit.AttachPreRaidBrief(situation, provider.GetService<ISituationPlaces>(), provider.GetService<IMapBossCatalog>());
        }

        return cockpit;
    }
}
