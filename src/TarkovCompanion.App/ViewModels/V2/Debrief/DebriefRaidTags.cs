using System.Text.Json;

namespace TarkovCompanion.App.ViewModels.V2.Debrief;

/// <summary>A player's add/remove history for one short raid tag.</summary>
/// <remarks>
/// Tags are events rather than another mutable raid column: removing one records the correction
/// instead of erasing that it was ever there, and the existing raid-event store needs no migration.
/// A malformed event is ignored so one hand-edited row cannot make Debrief unavailable.
/// </remarks>
internal static class DebriefRaidTags
{
    public const string EventType = "tag";
    public const int MaximumTagsPerRaid = 8;
    public const int MaximumTagLength = 24;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string? Normalize(string? value)
    {
        var tag = value?.Trim();
        return string.IsNullOrEmpty(tag)
            || tag.Length > MaximumTagLength
            || tag.Any(char.IsControl)
                ? null
                : tag;
    }

    public static string ToPayload(string tag, bool present) =>
        JsonSerializer.Serialize(new TagEvent(Normalize(tag) ?? throw new ArgumentException("Tag is not usable.", nameof(tag)), present), JsonOptions);

    public static IReadOnlyList<string> Resolve(IEnumerable<string> payloads)
    {
        var state = new Dictionary<string, (string Label, bool Present)>(StringComparer.OrdinalIgnoreCase);
        foreach (var payload in payloads)
        {
            try
            {
                var change = JsonSerializer.Deserialize<TagEvent>(payload, JsonOptions);
                if (Normalize(change?.Tag) is { } tag)
                {
                    state[tag] = (tag, change!.Present);
                }
            }
            catch (JsonException)
            {
                // One bad event is not a reason to hide every other tag on the raid.
            }
        }

        return
        [
            .. state.Values
                .Where(value => value.Present)
                .Select(value => value.Label)
                .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase)
                .Take(MaximumTagsPerRaid),
        ];
    }

    private sealed record TagEvent(string? Tag, bool Present);
}
