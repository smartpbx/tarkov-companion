using System.Text.Json;
using TarkovCompanion.App.Services.Diagnostics;

namespace TarkovCompanion.IntegrationTests.Simulator;

public sealed class SelfTestIntegrationTests
{
    [Fact]
    public async Task ProducesSuccessfulMachineReadableReportWithoutNetworkContact()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"tarkov-self-test-{Guid.NewGuid():N}.json");
        try
        {
            var report = await SelfTestRunner.RunAsync(outputPath, CancellationToken.None);

            Assert.True(report.Success);
            Assert.False(report.Environment.NetworkContacted);
            Assert.Equal("json.tarkov.dev", report.Environment.Provider);
            Assert.Equal(
                ["database", "cache", "search", "platform", "provider", "paths"],
                report.Checks.Select(check => check.Name));
            Assert.All(report.Checks, check => Assert.Equal("pass", check.Status));
            Assert.All(report.Safety, value => Assert.False(value.Value));

            await using var stream = File.OpenRead(outputPath);
            using var json = await JsonDocument.ParseAsync(stream);
            Assert.True(json.RootElement.GetProperty("success").GetBoolean());
        }
        finally
        {
            File.Delete(outputPath);
        }
    }
}
