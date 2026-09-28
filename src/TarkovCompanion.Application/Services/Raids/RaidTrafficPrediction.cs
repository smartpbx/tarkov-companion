using System.Text.Json;
using TarkovCompanion.Application.Services.Strategy;

namespace TarkovCompanion.Application.Services.Raids;

/// <summary>The exact model output the Raid workspace actually showed, retained for Debrief comparison.</summary>
public static class RaidTrafficPrediction
{
    public const string EventType = "historical-traffic-prediction";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string ToPayload(TrafficPredictionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return JsonSerializer.Serialize(receipt, JsonOptions);
    }

    /// <summary>Reads valid historical receipts without letting one damaged event hide the rest.</summary>
    public static IReadOnlyList<TrafficPredictionReceipt> ParseAll(IEnumerable<string> payloads)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        var receipts = new List<TrafficPredictionReceipt>();
        foreach (var payload in payloads)
        {
            try
            {
                if (JsonSerializer.Deserialize<TrafficPredictionReceipt>(payload, JsonOptions) is { } receipt)
                {
                    receipts.Add(receipt);
                }
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException)
            {
                // Historical events are independent evidence; skip only the malformed one.
            }
        }

        return receipts;
    }
}
