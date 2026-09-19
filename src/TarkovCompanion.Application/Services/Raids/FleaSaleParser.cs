using System.Text.Json;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Application.Services.Raids;

/// <summary>
/// Reads the notification the game posts when a flea offer sells.
/// </summary>
/// <remarks>
/// docs/research/EFT_LOG_FACTS.md once recorded that sales were absent, on the strength of a
/// search for the phrase "offer sold". The phrase never appears because the type name is one
/// word, and 52 of these notifications were sitting in the same files the whole time. The type
/// literal below is therefore matched exactly and case-sensitively, which is what that mistake
/// cost and what stops it recurring.
/// </remarks>
public static class FleaSaleParser
{
    /// <summary>The notification type, exactly as the game writes it.</summary>
    public const string NotificationMarker = "RagfairOfferSold";

    /// <summary>
    /// Reads one log line as a completed flea sale, or returns null.
    /// </summary>
    /// <remarks>
    /// Called on every line the watcher sees, so it rejects almost all of them on a single
    /// ordinal substring scan before the JSON parser is touched.
    /// </remarks>
    public static FleaSaleObservation? ParseLine(string? line, DateTimeOffset observedUtc)
    {
        if (string.IsNullOrEmpty(line) || !line.Contains(NotificationMarker, StringComparison.Ordinal))
        {
            return null;
        }

        // The payload is preceded by "NOTIFICATION <eventId> <type> " and that event id is
        // itself bracketed, so searching for a bare '[' finds the id and never the JSON.
        var start = line.IndexOf("[{", StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(line[start..]);
            if (document.RootElement.ValueKind != JsonValueKind.Array ||
                document.RootElement.GetArrayLength() == 0)
            {
                return null;
            }

            var payload = document.RootElement[0];
            if (!string.Equals(ReadText(payload, "type"), NotificationMarker, StringComparison.Ordinal))
            {
                return null;
            }

            // Without the offer id the same sale cannot be recognised when it is restated, and
            // a sale that multiplies on screen is worse than one that is missed.
            var offerId = ReadText(payload, "offerId");
            if (string.IsNullOrWhiteSpace(offerId))
            {
                return null;
            }

            return new FleaSaleObservation(
                offerId,
                ReadText(payload, "handbookId"),
                ReadCount(payload),
                observedUtc.ToUniversalTime());
        }
        catch (JsonException)
        {
            // A truncated or reshaped notification must not interrupt observation.
            return null;
        }
    }

    /// <summary>A sale with no stated quantity is one item, which is the common case.</summary>
    private static int ReadCount(JsonElement payload) =>
        payload.TryGetProperty("count", out var count) &&
        count.ValueKind == JsonValueKind.Number &&
        count.TryGetInt32(out var value) &&
        value > 0
            ? value
            : 1;

    private static string? ReadText(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>
/// Keeps the flea sales seen since the companion started.
/// </summary>
/// <remarks>
/// Deliberately not persisted. The game restates a notification when it is redelivered, and a
/// sale list that survived restarts would need a durable idea of which sales had already been
/// shown; within one session the offer id is enough. What the player wants from this is "what
/// sold while I was in that raid", which is a session-shaped question.
/// </remarks>
public sealed class FleaSaleStateService
{
    /// <summary>How many sales are kept. Older ones fall off the end.</summary>
    private const int MaximumSales = 100;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, FleaSaleObservation> _sales = new(StringComparer.Ordinal);

    public FleaSalesSnapshot Current { get; private set; } = FleaSalesSnapshot.Empty;

    public FleaSalesSnapshot Apply(FleaSaleObservation sale)
    {
        ArgumentNullException.ThrowIfNull(sale);
        lock (_gate)
        {
            // A redelivered notification is the same sale, not a second one.
            if (!_sales.TryAdd(sale.OfferId, sale))
            {
                return Current;
            }

            Current = new(
                _sales.Values
                    .OrderByDescending(observed => observed.ObservedUtc)
                    .Take(MaximumSales)
                    .ToArray(),
                sale.ObservedUtc);
            return Current;
        }
    }

    public FleaSalesSnapshot Clear()
    {
        lock (_gate)
        {
            _sales.Clear();
            Current = FleaSalesSnapshot.Empty;
            return Current;
        }
    }
}
