using System.Text.Json;
using System.Text.Json.Serialization;
using TarkovCompanion.Application.Services.Recommendations;
using TarkovCompanion.Core.Domain.Recommendations;

namespace TarkovCompanion.Infrastructure.Settings;

/// <summary>Stores recommendation look-ahead choices in <c>Config/recommendations.json</c>.</summary>
public sealed class JsonFileRecommendationPolicyStore(string settingsPath) : IRecommendationPolicyStore
{
    private const int MaximumSettingsBytes = 4 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<RecommendationHorizonSettings> GetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var file = new FileInfo(settingsPath);
            if (!file.Exists || file.Length is <= 0 or > MaximumSettingsBytes)
            {
                return RecommendationHorizonSettings.Default;
            }

            var json = await File.ReadAllTextAsync(settingsPath, cancellationToken).ConfigureAwait(false);
            var document = JsonSerializer.Deserialize<Document>(json, JsonOptions);
            return document is null || document.SchemaVersion > RecommendationHorizonSettings.SchemaVersion
                ? RecommendationHorizonSettings.Default
                : new RecommendationHorizonSettings(
                    document.Quest ?? RecommendationHorizonSettings.Default.Quest,
                    document.Hideout ?? RecommendationHorizonSettings.Default.Hideout).Normalized();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return RecommendationHorizonSettings.Default;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(RecommendationHorizonSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var normalized = settings.Normalized();
            var document = new Document(
                RecommendationHorizonSettings.SchemaVersion,
                normalized.Quest,
                normalized.Hideout);
            await AtomicJsonFile.WriteAsync(
                settingsPath,
                JsonSerializer.Serialize(document, JsonOptions),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed record Document(
        int SchemaVersion,
        RecommendationHorizon? Quest,
        RecommendationHorizon? Hideout);
}
