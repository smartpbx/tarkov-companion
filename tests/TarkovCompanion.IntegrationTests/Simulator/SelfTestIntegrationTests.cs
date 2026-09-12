using System.Text.Json;
using Microsoft.Data.Sqlite;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.Diagnostics;

namespace TarkovCompanion.IntegrationTests.Simulator;

public sealed class SelfTestIntegrationTests
{
    [Fact]
    public async Task ProducesCompositionBackedReportWithoutNetworkContact()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tarkov-self-test-{Guid.NewGuid():N}");
        var outputPath = Path.Combine(root, "self-test.json");
        try
        {
            var commandLine = new AppCommandLine(
                SelfTest: true,
                Demo: true,
                Headless: true,
                DeveloperMode: false,
                OutputPath: outputPath,
                DemoFixturePath: null,
                DiagnosticChannelPath: null);
            var report = await SelfTestRunner.RunAsync(
                outputPath,
                commandLine,
                new AppCompositionSettings(DataRoot: root),
                CancellationToken.None);

            Assert.True(report.Success);
            Assert.False(report.Environment.NetworkContacted);
            Assert.Equal("json.tarkov.dev", report.Environment.Provider);
            Assert.Equal(
                [
                    "database",
                    "cache",
                    "data-source",
                    "profile",
                    "ocr-provider",
                    "recognition-catalog",
                    "recognition-icon-fallback",
                    "scan-hotkey",
                    "diagnostic",
                    "paths",
                    "platform",
                ],
                report.Checks.Select(check => check.Name));
            Assert.Equal("pass", report.Checks.Single(check => check.Name == "database").Status);
            Assert.Equal("pass", report.Checks.Single(check => check.Name == "cache").Status);
            Assert.Equal(
                OperatingSystem.IsWindows() ? "pass" : "unavailable",
                report.Checks.Single(check => check.Name == "ocr-provider").Status);
            Assert.Equal("pass", report.Checks.Single(check => check.Name == "recognition-catalog").Status);
            Assert.Equal("unavailable", report.Checks.Single(check => check.Name == "recognition-icon-fallback").Status);

            // The shortcut is registered for real during the self-test, which is the only way
            // to prove the combination is obtainable. Global shortcuts do not exist off
            // Windows, so there it reports unavailable rather than failing.
            var hotkey = report.Checks.Single(check => check.Name == "scan-hotkey");
            Assert.False(hotkey.Required);
            Assert.Contains("Ctrl + Alt + S", hotkey.Detail, StringComparison.Ordinal);
            Assert.Equal(OperatingSystem.IsWindows() ? "pass" : "unavailable", hotkey.Status);
            Assert.False(report.Safety["readsGameMemory"]);
            Assert.False(report.Safety["sendsGameInput"]);
            Assert.False(report.Safety["capturesNetworkTraffic"]);
            Assert.False(report.Safety["diagnosticChannelEnabled"]);

            await using var stream = File.OpenRead(outputPath);
            using var json = await JsonDocument.ParseAsync(stream);
            Assert.True(json.RootElement.GetProperty("success").GetBoolean());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                ScratchDirectory.Remove(root);
            }
        }
    }
}
