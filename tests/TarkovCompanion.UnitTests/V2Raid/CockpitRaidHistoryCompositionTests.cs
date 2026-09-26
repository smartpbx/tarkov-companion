using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels.V2.Raid;

namespace TarkovCompanion.UnitTests.V2Raid;

/// <summary>
/// The cockpit takes raid history as an optional argument so galleries stay inert, and the app
/// never passed it: a chosen suggested route was not saved for Debrief in any build. Found while
/// wiring the player's own pace into the Now panel (#969).
/// </summary>
public sealed class CockpitRaidHistoryCompositionTests
{
    [Fact]
    public async Task The_composed_app_saves_a_chosen_route_to_the_raid()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tarkov-route-history-{Guid.NewGuid():N}");
        try
        {
            await using var services = AppComposition.Build(
                new AppCommandLine(false, true, false, false, null, null, null) { UiShell = V2ShellMode.VariantA },
                new(DataRoot: root, Offline: true));
            Assert.True(services.GetRequiredService<RaidCockpitViewModel>().RecordsPlannedRoutes);
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}
