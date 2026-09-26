using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TarkovCompanion.Application.Services.FormatGuards;
using TarkovCompanion.Application.Services.Recognition;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Application.Services.Situations;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Infrastructure.Maps;

namespace TarkovCompanion.App.Services.V2;

/// <summary>[#712 0-2] Registers the situation engine (ADR 0022): one call from AppComposition.</summary>
/// <remarks>
/// The log observer, the shell and the clock-jump wiring take the service optionally, so a
/// composition without this still composes; with it, the one instance is fed by all of them.
/// </remarks>
internal static class SituationComposition
{
    public static void Add(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<ISituationPlaces>(provider =>
        {
            var catalog = provider.GetRequiredService<TarkovDevMapCatalogClient>();
            return new CatalogSituationPlaces(
                async cancellationToken => (await catalog.GetAsync(cancellationToken).ConfigureAwait(false)).Catalog,
                provider.GetService<IMapDataService>());
        });
        // [#712 0-3] One monitor: the log and screenshot watchers feed it, the situation and Setup read it.
        services.AddSingleton(provider => new FormatHealthMonitor(
            provider.GetService<TimeProvider>(),
            provider.GetService<ILogger<FormatHealthMonitor>>()));
        // [#712 1-1] Every screenshot's routing decision: the tray lists the unsure ones and the
        // situation says which detector placed the rest.
        services.AddSingleton<Application.Services.CaptureSessions.ScreenRoutingLog>();
        services.AddSingleton(provider => new Capture.UnrecognisedScreenTray(
            provider.GetRequiredService<Application.Services.CaptureSessions.ScreenRoutingLog>()));
        services.AddSingleton(provider => new SituationService(
            provider.GetRequiredService<IRuntimeStateStore>(),
            provider.GetService<TimeProvider>(),
            provider.GetRequiredService<ISituationPlaces>(),
            // Registered only where recognition is (Windows); elsewhere LAST SCAN stays empty.
            provider.GetService<LatestScanResultPublisher>(),
            provider.GetService<ILogger<SituationService>>(),
            formatHealth: provider.GetRequiredService<FormatHealthMonitor>(),
            routing: provider.GetRequiredService<Application.Services.CaptureSessions.ScreenRoutingLog>()));
    }
}
