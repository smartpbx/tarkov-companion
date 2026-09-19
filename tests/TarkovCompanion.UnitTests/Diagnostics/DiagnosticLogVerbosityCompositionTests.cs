using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.Infrastructure.Diagnostics;

namespace TarkovCompanion.UnitTests.Diagnostics;

/// <summary>
/// The log level the running app uses comes from <c>TARKOV_COMPANION_DIAGNOSTIC_LOG_LEVEL</c>.
/// </summary>
/// <remarks>
/// <c>DiagnosticRuntimeControls.FromEnvironment</c> was written and tested and read by nothing:
/// composition fixed <c>LogLevel.Information</c>, and both log providers hard-filtered at
/// Information as well, so even a composition that asked for Debug would have got none. These build
/// the real composition with an environment supplied to it, so no test sets a process-wide variable,
/// and ask the real logger what it will write.
/// </remarks>
public sealed class DiagnosticLogVerbosityCompositionTests
{
    [Theory]
    [InlineData(null, LogLevel.Information, LogLevel.Debug)]
    [InlineData("nonsense", LogLevel.Information, LogLevel.Debug)]
    [InlineData("Debug", LogLevel.Debug, LogLevel.Trace)]
    [InlineData("TRACE", LogLevel.Trace, null)]
    [InlineData("Warning", LogLevel.Warning, LogLevel.Information)]
    [InlineData("error", LogLevel.Error, LogLevel.Warning)]
    public async Task TheRunningComposition_WritesFromTheConfiguredLevelUpAndNothingBelow(
        string? configured, LogLevel lowestWritten, LogLevel? highestSuppressed)
    {
        await using var services = Compose(configured);
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("verbosity-test");

        Assert.True(logger.IsEnabled(lowestWritten), $"{lowestWritten} should be written at '{configured}'");
        if (highestSuppressed is { } suppressed)
        {
            Assert.False(logger.IsEnabled(suppressed), $"{suppressed} should be suppressed at '{configured}'");
        }
    }

    [Theory]
    [InlineData(DiagnosticLogVerbosity.Trace, LogLevel.Trace)]
    [InlineData(DiagnosticLogVerbosity.Debug, LogLevel.Debug)]
    [InlineData(DiagnosticLogVerbosity.Information, LogLevel.Information)]
    [InlineData(DiagnosticLogVerbosity.Warning, LogLevel.Warning)]
    [InlineData(DiagnosticLogVerbosity.Error, LogLevel.Error)]
    public void EveryVerbosityMapsToItsOwnLogLevel(DiagnosticLogVerbosity verbosity, LogLevel expected) =>
        Assert.Equal(expected, new DiagnosticRuntimeControls(verbosity, false).MinimumLogLevel);

    private static ServiceProvider Compose(string? logLevel) => AppComposition.Build(
        new AppCommandLine(false, true, false, false, null, null, null),
        new(
            DataRoot: Path.Combine(Path.GetTempPath(), $"tarkov-verbosity-{Guid.NewGuid():N}"),
            Offline: true,
            ReadEnvironment: name => name == "TARKOV_COMPANION_DIAGNOSTIC_LOG_LEVEL" ? logLevel : null));
}
