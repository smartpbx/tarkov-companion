using System.Text.Json;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.Application.Services.Raids;

/// <summary>One quest the game says has started, failed or finished.</summary>
/// <param name="EventId">The notification's own id, so a redelivered one is not acted on twice.</param>
/// <param name="TaskId">The quest, by the id the catalog uses.</param>
/// <param name="State">What the game says has happened to it.</param>
/// <param name="ObservedUtc">When the line was read.</param>
public sealed record QuestStatusObservation(
    string EventId,
    string TaskId,
    RecordedTaskState State,
    DateTimeOffset ObservedUtc);

/// <summary>
/// Reads the message the game posts when a quest starts, fails or is handed in.
/// </summary>
/// <remarks>
/// Every quest on the page read Unknown, because the stored progress was created empty and
/// nothing ever wrote to it again. The two ways in were a JSON file and a TarkovTracker token,
/// both of which a player has to know about and go and do.
///
/// The game has been saying so all along. Each of these arrives as a <c>ChatMessageReceived</c>
/// notification in the same files the flea sales come from, with a message type of 10 for
/// started, 11 for failed and 12 for finished, and a template id whose first word is the
/// quest's own id. Nothing here is inferred: these are the game's own words about the player's
/// own quests.
///
/// The objectives are not in it. This moves a quest between states and leaves what is recorded
/// against each objective alone, which is the player's.
/// </remarks>
public static class QuestNotificationParser
{
    /// <summary>
    /// How the line announces itself, which is the cheap way to reject the other ninety-nine.
    /// </summary>
    /// <remarks>
    /// This is the announcement, not the payload. The payload inside calls itself
    /// "new_message", and an earlier version compared one against the other and therefore
    /// recognised nothing at all. Verified against a live install: 380 of these lines across
    /// eight log folders, every one of them with a "new_message" payload.
    /// </remarks>
    public const string NotificationMarker = "ChatMessageReceived";

    /// <summary>
    /// A quest id is a twenty-four character identifier.
    /// </summary>
    /// <remarks>
    /// Checked because the same template id shape carries system messages that are not about a
    /// quest at all, and a short first word is how one of those is told from a quest id.
    /// </remarks>
    private const int MinimumTaskIdLength = 20;

    /// <summary>
    /// Reads one log line as a quest changing state, or returns null.
    /// </summary>
    /// <remarks>
    /// Called on every line the watcher sees, so it rejects almost all of them on a single
    /// ordinal substring scan before the JSON parser is touched.
    /// </remarks>
    public static QuestStatusObservation? ParseLine(string? line, DateTimeOffset observedUtc)
    {
        if (string.IsNullOrEmpty(line) || !line.Contains(NotificationMarker, StringComparison.Ordinal))
        {
            return null;
        }

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

            // Matched on the shape rather than on the payload's own type. The line is
            // announced as ChatMessageReceived and the payload inside it calls itself
            // "new_message", and checking the payload against the announcement rejected every
            // notification the game writes. A message object carrying one of three numeric
            // types and a template id is the thing being looked for, and nothing else has it.
            var payload = document.RootElement[0];
            if (!payload.TryGetProperty("message", out var message) ||
                message.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (ReadState(message) is not { } state)
            {
                return null;
            }

            var templateId = ReadText(message, "templateId");
            if (string.IsNullOrWhiteSpace(templateId))
            {
                return null;
            }

            var taskId = templateId.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (taskId is null || taskId.Length < MinimumTaskIdLength)
            {
                return null;
            }

            // The game restates a notification when it redelivers one, and every message
            // carries its own id. Without it the same quest would be reapplied on every
            // restart, which matters because an old "started" arriving after a "finished"
            // would otherwise undo the hand-in.
            var eventId = ReadText(message, "_id") ?? $"{taskId}:{state}";
            return new(eventId, taskId, state, observedUtc.ToUniversalTime());
        }
        catch (JsonException)
        {
            // A truncated or reshaped notification must not interrupt observation.
            return null;
        }
    }

    /// <summary>
    /// The three message types that are about a quest, and nothing else.
    /// </summary>
    /// <remarks>
    /// The same notification carries flea sales, insurance returns and ordinary chat, so the
    /// number is what keeps those out.
    ///
    /// It is also the only field that tells the truth. The message's own <c>text</c> reads
    /// "quest started" on all three, including the hand-in and the failure, and the template
    /// id's second word is "description" on a start and "successMessageText" on a completion
    /// but simply "0" on a failure. Both look authoritative and both would have marked every
    /// quest as started.
    /// </remarks>
    private static RecordedTaskState? ReadState(JsonElement message) =>
        message.TryGetProperty("type", out var type) &&
        type.ValueKind == JsonValueKind.Number &&
        type.TryGetInt32(out var value)
            ? value switch
            {
                10 => RecordedTaskState.Active,
                11 => RecordedTaskState.Failed,
                12 => RecordedTaskState.Completed,
                _ => null,
            }
            : null;

    private static string? ReadText(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
