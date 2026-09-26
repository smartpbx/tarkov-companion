using System;
using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.ViewModels.V2.Ask;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.Application.Services.Ask;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Intel;
using TarkovCompanion.Core.Abstractions;

namespace TarkovCompanion.App.Services.V2;

/// <summary>[#712 2-5] Registers the Ask box: one call from AppComposition.</summary>
/// <remarks>
/// Only the rules source is registered. A local model (decision 3) would be a second
/// <see cref="IAnswerSource"/> of kind LocalModel plus a Setup switch behind
/// <see cref="IAskSettings"/>; until one exists the setting reads off, and nothing leaves the PC.
/// </remarks>
internal static class AskComposition
{
    public static void Add(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IAskRaidSource>(provider => new RaidCockpitAskSource(
            new Lazy<RaidCockpitViewModel?>(provider.GetService<RaidCockpitViewModel>)));
        services.AddSingleton<IAskSettings, LocalModelOff>();
        services.AddSingleton<IAnswerSource>(provider => new RulesAnswerSource(
            provider.GetRequiredService<IItemRepository>(),
            provider.GetService<IItemIntelService>(),
            provider.GetService<IQuestReadService>(),
            provider.GetService<IPlayerProfileService>(),
            provider.GetService<IRequirementCatalog>(),
            provider.GetService<IHideoutPrerequisiteCatalog>(),
            provider.GetService<IIntelTradeCatalogService>(),
            provider.GetService<IItemFactCatalog>(),
            provider.GetService<IItemAcquisitionService>(),
            provider.GetRequiredService<IAskRaidSource>()));
        services.AddSingleton(provider => new AskService(
            provider.GetServices<IAnswerSource>(),
            provider.GetService<IAskSettings>()));
        services.AddSingleton(provider => new AskViewModel(
            provider.GetRequiredService<AskService>(),
            provider.GetService<TimeProvider>()));
    }

    /// <summary>The Raid cockpit's exits, read when a question is asked; the cockpit is built once.</summary>
    private sealed class RaidCockpitAskSource(Lazy<RaidCockpitViewModel?> cockpit) : IAskRaidSource
    {
        public AskRaidSnapshot? Current() => cockpit.Value?.AskSnapshot();
    }

    /// <summary>No local model ships yet, so the switch is off (decision 3: off by default).</summary>
    private sealed class LocalModelOff : IAskSettings
    {
        public bool LocalModelEnabled => false;
    }
}
