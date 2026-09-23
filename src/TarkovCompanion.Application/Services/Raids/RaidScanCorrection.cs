using System.Text.Json;

namespace TarkovCompanion.Application.Services.Raids;

/// <summary>A player's correction that one recorded scan should not count.</summary>
/// <remarks>
/// The original scan remains immutable. Corrections are append-only, and the latest correction
/// for its stable event id decides whether it contributes to totals. This also lets a mistaken
/// correction be restored without pretending it never happened.
/// </remarks>
public sealed record RaidScanCorrection(string ScanId, bool IsWrong, DateTimeOffset CorrectedUtc)
{
    public const string EventType = "scan-correction";
    private const int MaximumScanIdLength = 128;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string ToPayload()
    {
        Validate(ScanId, CorrectedUtc);
        return JsonSerializer.Serialize(this, JsonOptions);
    }

    public static IReadOnlySet<string> WrongScanIds(IEnumerable<string> payloads)
    {
        var state = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var payload in payloads)
        {
            try
            {
                var correction = JsonSerializer.Deserialize<RaidScanCorrection>(payload, JsonOptions);
                if (correction is not null && IsValid(correction.ScanId, correction.CorrectedUtc))
                {
                    state[correction.ScanId] = correction.IsWrong;
                }
            }
            catch (JsonException)
            {
                // One malformed correction cannot hide or restore another scan.
            }
        }

        return state.Where(entry => entry.Value).Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
    }

    private static void Validate(string scanId, DateTimeOffset correctedUtc)
    {
        if (!IsValid(scanId, correctedUtc))
        {
            throw new ArgumentException("A scan correction needs a bounded event id and a UTC correction time.");
        }
    }

    private static bool IsValid(string? scanId, DateTimeOffset correctedUtc) =>
        !string.IsNullOrWhiteSpace(scanId)
        && scanId.Length <= MaximumScanIdLength
        && !scanId.Any(char.IsControl)
        && correctedUtc != default
        && correctedUtc.Offset == TimeSpan.Zero;
}
