using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.Shell;
using TarkovCompanion.Core.Abstractions;
using Xunit.Abstractions;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// #786: the application's own services, torn down the way <c>Program.ShutDown</c> tears them down.
/// </summary>
/// <remarks>
/// The "services" stage of teardown is one <c>ServiceProvider.DisposeAsync</c> over everything
/// composed, so one service that waits on its way out holds all of it. On Windows that stage used
/// the whole eight-second budget when the window was closed during startup, and the process was
/// then killed by the fifteen-second exit deadline. Composed here exactly as the app composes it.
/// </remarks>
public sealed class AppTeardownTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ClosingDuringStartupTearsTheServicesDownWithinASecond()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), $"tarkov-teardown-{Guid.NewGuid():N}");
        var services = AppComposition.Build(
            new AppCommandLine(false, false, false, false, null, null, null) { UiShell = V2ShellMode.VariantA },
            new(DataRoot: dataRoot, Offline: true));
        try
        {
            // What a launch starts before the first page has loaded; the database is not migrated
            // yet, which is where a close during startup finds it.
            var main = services.GetRequiredService<MainWindowViewModel>();
            main.PreviewShell = services.GetRequiredService<V2ShellViewModel>();
            services.GetRequiredService<DatabaseMaintenanceCoordinator>().Start();
            services.GetRequiredService<IRaidHistoryService>();

            var watch = Stopwatch.StartNew();
            await services.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
            watch.Stop();
            output.WriteLine($"services {watch.ElapsedMilliseconds} ms");

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"Service teardown took {watch.ElapsedMilliseconds} ms.");
        }
        finally
        {
            try
            {
                Directory.Delete(dataRoot, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
