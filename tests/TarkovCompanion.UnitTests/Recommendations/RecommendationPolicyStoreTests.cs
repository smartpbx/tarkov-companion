using TarkovCompanion.Core.Domain.Recommendations;
using TarkovCompanion.Infrastructure.Settings;

namespace TarkovCompanion.UnitTests.Recommendations;

public sealed class RecommendationPolicyStoreTests
{
    [Fact]
    public async Task QuestAndHideoutHorizonsSurviveAStoreRestart()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("recommendations.json");
        var wanted = new RecommendationHorizonSettings(
            RecommendationHorizon.All,
            RecommendationHorizon.NextOnly);

        await new JsonFileRecommendationPolicyStore(path).SaveAsync(wanted, CancellationToken.None);
        var reopened = new JsonFileRecommendationPolicyStore(path);

        Assert.Equal(wanted, await reopened.GetAsync(CancellationToken.None));
        var json = await File.ReadAllTextAsync(path, CancellationToken.None);
        Assert.Contains("\"quest\": \"All\"", json, StringComparison.Ordinal);
        Assert.Contains("\"hideout\": \"NextOnly\"", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{\"schemaVersion\":99,\"quest\":\"All\"}")]
    public async Task UnreadableOrNewerSettingsUseTheDocumentedDefaults(string json)
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("recommendations.json");
        await File.WriteAllTextAsync(path, json, CancellationToken.None);

        var result = await new JsonFileRecommendationPolicyStore(path).GetAsync(CancellationToken.None);

        Assert.Equal(RecommendationHorizonSettings.Default, result);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _path = Path.Combine(
            Path.GetTempPath(),
            $"tarkov-recommendation-policy-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(_path);

        public string File(string name) => Path.Combine(_path, name);

        public void Dispose() => Directory.Delete(_path, recursive: true);
    }
}
