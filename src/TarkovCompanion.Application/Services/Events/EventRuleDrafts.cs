using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TarkovCompanion.Application.Services.Events;

/// <summary>The effects the Events editor can write, one per rule type the parser knows.</summary>
public enum EventEffectKind
{
    TraderPriceMultiplier,
    FleaAvailability,
    MapAvailability,
    BossSpawnMultiplier,
    QuestAvailabilityWindow,

    /// <summary>A type this build does not know. Kept as written so it can be seen and removed.</summary>
    Unknown,
}

/// <summary>
/// One effect as the editor holds it: text where a person types, so a half-typed value is still
/// something the validator can point at rather than something the editor had to throw away.
/// </summary>
/// <remarks>
/// <see cref="StartUtc"/> and <see cref="StartRaw"/> are either/or: a date the editor could read
/// travels as an instant, one it could not travels as the text, and the parser refuses the text
/// with the same path a hand-written file would get.
/// </remarks>
public sealed record EventEffectDraft(
    EventEffectKind Kind,
    string TargetId = "",
    string TargetName = "",
    string Multiplier = "",
    bool Enabled = false,
    DateTimeOffset? StartUtc = null,
    string? StartRaw = null,
    DateTimeOffset? EndUtc = null,
    string? EndRaw = null,
    string? MapId = null,
    string? UnknownJson = null);

/// <summary>The drafts written out, and what the rule parser said about the result.</summary>
public sealed record EventRuleDraftResult(string RulesJson, EventRuleParseResult Parsed)
{
    public bool IsValid => Parsed.IsValid;

    /// <summary>The issues that belong to one draft, by its position, with the field they name.</summary>
    /// <remarks>
    /// The drafts are written in order, so <c>$.effects[3].multiplier</c> is the fourth row's
    /// multiplier. The field is empty for an issue about the row as a whole (a quest window with
    /// neither end), and issues above the rows (<c>$</c>, <c>$.effects</c>) are not returned here.
    /// </remarks>
    public IReadOnlyList<(string Field, string Message)> IssuesFor(int index)
    {
        var prefix = $"$.effects[{index.ToString(CultureInfo.InvariantCulture)}]";
        var issues = new List<(string, string)>();
        foreach (var issue in Parsed.Issues)
        {
            if (!issue.Path.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = issue.Path[prefix.Length..];
            if (rest.Length == 0)
            {
                issues.Add((string.Empty, issue.Message));
            }
            else if (rest[0] == '.')
            {
                issues.Add((rest[1..], issue.Message));
            }
        }

        return issues;
    }

    public IEnumerable<EventRuleValidationIssue> GeneralIssues => Parsed.Issues.Where(issue =>
        !issue.Path.StartsWith("$.effects[", StringComparison.Ordinal));
}

/// <summary>
/// Reads a stored rule set into editable drafts and writes drafts back, keeping the parser as the
/// only judge of what is valid.
/// </summary>
/// <remarks>
/// <para>
/// The editor does not carry its own rules about what a valid effect is. It writes exactly what the
/// person typed into the file format and asks <see cref="EventRuleParser"/>, so the page and the
/// recommendation engine cannot disagree about whether a rule is in force.
/// </para>
/// <para>
/// Reading is lenient on purpose. A hand-written file with a missing map id still opens as a row
/// with an empty map, marked where it is wrong, instead of vanishing from the editor and being
/// dropped on the next save.
/// </para>
/// </remarks>
public static class EventRuleDrafts
{
    public static string TypeName(EventEffectKind kind) => kind switch
    {
        EventEffectKind.TraderPriceMultiplier => "trader-price-multiplier",
        EventEffectKind.FleaAvailability => "flea-availability",
        EventEffectKind.MapAvailability => "map-availability",
        EventEffectKind.BossSpawnMultiplier => "boss-spawn-multiplier",
        EventEffectKind.QuestAvailabilityWindow => "quest-availability-window",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>The stored effects as drafts, or null when the text is not a JSON object at all.</summary>
    public static IReadOnlyList<EventEffectDraft>? Read(string? rulesJson)
    {
        if (string.IsNullOrWhiteSpace(rulesJson))
        {
            return [];
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(rulesJson, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 16,
            });
        }
        catch (JsonException)
        {
            return null;
        }

        if (root is not JsonObject rootObject)
        {
            return null;
        }

        if (rootObject["effects"] is not JsonArray effects)
        {
            return rootObject.ContainsKey("effects") ? null : [];
        }

        return effects.Select(ReadEffect).ToArray();
    }

    /// <summary>
    /// Writes the drafts as the stored rule text, keeping any other root property the original had
    /// (older files carry metadata the parser tolerates and this editor has no reason to delete).
    /// </summary>
    public static string Write(string? originalJson, IReadOnlyList<EventEffectDraft> drafts)
    {
        ArgumentNullException.ThrowIfNull(drafts);
        var root = OriginalRoot(originalJson);
        var effects = new JsonArray();
        foreach (var draft in drafts)
        {
            effects.Add(WriteEffect(draft));
        }

        root["effects"] = effects;
        return root.ToJsonString();
    }

    /// <summary>Writes the drafts and runs the parser over the result.</summary>
    public static EventRuleDraftResult Validate(string? originalJson, IReadOnlyList<EventEffectDraft> drafts)
    {
        var json = Write(originalJson, drafts);
        return new(json, EventRuleParser.Parse(json));
    }

    private static JsonObject OriginalRoot(string? originalJson)
    {
        if (string.IsNullOrWhiteSpace(originalJson))
        {
            return [];
        }

        try
        {
            return JsonNode.Parse(originalJson, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 16,
            }) is JsonObject root
                ? root
                : [];
        }
        catch (JsonException)
        {
            // Text that was never JSON has nothing worth keeping; the editor says it replaced it.
            return [];
        }
    }

    private static EventEffectDraft ReadEffect(JsonNode? node)
    {
        if (node is not JsonObject effect)
        {
            return new(EventEffectKind.Unknown, UnknownJson: node?.ToJsonString() ?? "null");
        }

        return Text(effect, "type") switch
        {
            "trader-price-multiplier" => new(
                EventEffectKind.TraderPriceMultiplier,
                Text(effect, "traderId"),
                Text(effect, "traderName"),
                Number(effect, "multiplier")),
            "flea-availability" => new(EventEffectKind.FleaAvailability, Enabled: Flag(effect, "enabled")),
            "map-availability" => new(
                EventEffectKind.MapAvailability,
                Text(effect, "mapId"),
                Text(effect, "mapName"),
                Enabled: Flag(effect, "available")),
            "boss-spawn-multiplier" => new(
                EventEffectKind.BossSpawnMultiplier,
                Text(effect, "bossId"),
                Text(effect, "bossName"),
                Number(effect, "multiplier"),
                MapId: NullIfEmpty(Text(effect, "mapId"))),
            "quest-availability-window" => ReadQuestWindow(effect),
            _ => new(EventEffectKind.Unknown, UnknownJson: effect.ToJsonString()),
        };
    }

    private static EventEffectDraft ReadQuestWindow(JsonObject effect)
    {
        var (start, startRaw) = Instant(effect, "startUtc");
        var (end, endRaw) = Instant(effect, "endUtc");
        return new(
            EventEffectKind.QuestAvailabilityWindow,
            Text(effect, "questId"),
            Text(effect, "questName"),
            StartUtc: start,
            StartRaw: startRaw,
            EndUtc: end,
            EndRaw: endRaw);
    }

    private static JsonNode WriteEffect(EventEffectDraft draft)
    {
        if (draft.Kind == EventEffectKind.Unknown)
        {
            return JsonNode.Parse(draft.UnknownJson ?? "null") ?? new JsonObject();
        }

        var effect = new JsonObject { ["type"] = TypeName(draft.Kind) };
        switch (draft.Kind)
        {
            case EventEffectKind.TraderPriceMultiplier:
                effect["traderId"] = draft.TargetId.Trim();
                Name(effect, "traderName", draft.TargetName);
                effect["multiplier"] = MultiplierNode(draft.Multiplier);
                break;
            case EventEffectKind.FleaAvailability:
                effect["enabled"] = draft.Enabled;
                break;
            case EventEffectKind.MapAvailability:
                effect["mapId"] = draft.TargetId.Trim();
                Name(effect, "mapName", draft.TargetName);
                effect["available"] = draft.Enabled;
                break;
            case EventEffectKind.BossSpawnMultiplier:
                effect["bossId"] = draft.TargetId.Trim();
                Name(effect, "bossName", draft.TargetName);
                if (!string.IsNullOrWhiteSpace(draft.MapId))
                {
                    effect["mapId"] = draft.MapId.Trim();
                }

                effect["multiplier"] = MultiplierNode(draft.Multiplier);
                break;
            case EventEffectKind.QuestAvailabilityWindow:
                effect["questId"] = draft.TargetId.Trim();
                Name(effect, "questName", draft.TargetName);
                WriteInstant(effect, "startUtc", draft.StartUtc, draft.StartRaw);
                WriteInstant(effect, "endUtc", draft.EndUtc, draft.EndRaw);
                break;
        }

        return effect;
    }

    /// <summary>A number when the text reads as one, otherwise the text, for the parser to refuse.</summary>
    /// <remarks>
    /// Both the player's culture and the invariant one are tried, so "0,8" and "0.8" both mean
    /// eight tenths on a German machine; an empty box is written as an empty string, which the
    /// parser reports as a missing multiplier.
    /// </remarks>
    private static JsonNode MultiplierNode(string text)
    {
        var trimmed = text.Trim().TrimStart('x', 'X', '×');
        return decimal.TryParse(trimmed, NumberStyles.Number, CultureInfo.CurrentCulture, out var value) ||
               decimal.TryParse(trimmed, NumberStyles.Number, CultureInfo.InvariantCulture, out value)
            ? JsonValue.Create(value)
            : JsonValue.Create(text);
    }

    private static void WriteInstant(JsonObject effect, string name, DateTimeOffset? instant, string? raw)
    {
        if (instant is { } value)
        {
            effect[name] = value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }
        else if (!string.IsNullOrWhiteSpace(raw))
        {
            effect[name] = raw;
        }
    }

    private static void Name(JsonObject effect, string property, string name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            effect[property] = name.Trim();
        }
    }

    private static string Text(JsonObject effect, string name) =>
        effect[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;

    private static string Number(JsonObject effect, string name) => effect[name] switch
    {
        JsonValue value when value.TryGetValue<decimal>(out var number) => number.ToString(CultureInfo.CurrentCulture),
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        _ => string.Empty,
    };

    private static bool Flag(JsonObject effect, string name) =>
        effect[name] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    private static (DateTimeOffset? Instant, string? Raw) Instant(JsonObject effect, string name)
    {
        var text = Text(effect, name);
        if (text.Length == 0)
        {
            return (null, null);
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) &&
               (text.EndsWith('Z') || text.Contains('+') || text.LastIndexOf('-') > 10)
            ? (parsed.ToUniversalTime(), null)
            : (null, text);
    }

    private static string? NullIfEmpty(string text) => text.Length == 0 ? null : text;
}
