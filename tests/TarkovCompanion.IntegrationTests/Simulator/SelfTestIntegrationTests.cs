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
                ["database", "cache", "data-source", "profile", "ocr-provider", "diagnostic", "paths", "platform"],
                report.Checks.Select(check => check.Name));
            Assert.Equal("pass", report.Checks.Single(check => check.Name == "database").Status);
            Assert.Equal("pass", report.Checks.Single(check => check.Name == "cache").Status);
            Assert.Equal("unavailable", report.Checks.Single(check => check.Name == "ocr-provider").Status);
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
                Directory.Delete(root, true);
            }
        }
    }
}
