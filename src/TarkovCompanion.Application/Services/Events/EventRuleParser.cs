using System.Globalization;
using System.Text.Json;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Items;

namespace TarkovCompanion.Application.Services.Events;

public sealed record EventRuleValidationIssue(string Path, string Message)
{
    public override string ToString() => $"{Path}: {Message}";
}

public sealed record EventRuleParseResult(
    EventRuleSet Rules,
    IReadOnlyList<EventRuleValidationIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;
}

public sealed record EventRuleEvaluation(
    ActiveEventRules Active,
    IReadOnlyDictionary<string, IReadOnlyList<EventRuleValidationIssue>> InvalidDefinitions);

/// <summary>
/// Parses the deliberately small event-rule language. Properties the current app does not know
/// are ignored for forward compatibility; an unknown effect type is refused because silently
/// ignoring an effect would make the preview disagree with what the author thought was active.
/// </summary>
public static class EventRuleParser
{
    private const int MaximumEffects = 128;
    private const int MaximumTextLength = 128;

    public static EventRuleParseResult Parse(string? rulesJson)
    {
        if (string.IsNullOrWhiteSpace(rulesJson))
        {
            return new(EventRuleSet.Empty, []);
        }

        try
        {
            using var document = JsonDocument.Parse(rulesJson, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 16,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Invalid("$", "rules must be a JSON object");
            }

            if (!document.RootElement.TryGetProperty("effects", out var effects))
            {
                // Existing definitions use {} or carry legacy consumePrecedence metadata. Both
                // mean no typed effects, and extra root properties remain forward-compatible.
                return new(EventRuleSet.Empty, []);
            }

            if (effects.ValueKind != JsonValueKind.Array)
            {
                return Invalid("$.effects", "must be an array");
            }

            var parsed = new List<EventRuleEffect>();
            var issues = new List<EventRuleValidationIssue>();
            var index = 0;
            foreach (var effect in effects.EnumerateArray())
            {
                var path = $"$.effects[{index}]";
                if (index >= MaximumEffects)
                {
                    issues.Add(new("$.effects", $"cannot contain more than {MaximumEffects} effects"));
                    break;
                }

                ParseEffect(effect, path, parsed, issues);
                index++;
            }

            return new(new EventRuleSet(parsed.AsReadOnly()), issues.AsReadOnly());
        }
        catch (JsonException exception)
        {
            return Invalid("$", $"invalid JSON at byte {exception.BytePositionInLine}: {exception.Message}");
        }
    }

    private static EventRuleParseResult Invalid(string path, string message) =>
        new(EventRuleSet.Empty, [new(path, message)]);

    private static void ParseEffect(
        JsonElement effect,
        string path,
        List<EventRuleEffect> parsed,
        List<EventRuleValidationIssue> issues)
    {
        if (effect.ValueKind != JsonValueKind.Object)
        {
            issues.Add(new(path, "must be an object"));
            return;
        }

        if (!RequiredString(effect, "type", path, issues, out var type))
        {
            return;
        }

        switch (type)
        {
            case "trader-price-multiplier":
                if (RequiredString(effect, "traderId", path, issues, out var traderId) &&
                    PositiveMultiplier(effect, "multiplier", path, issues, out var traderMultiplier))
                {
                    parsed.Add(new TraderPriceMultiplierRule(
                        traderId,
                        OptionalString(effect, "traderName", path, issues) ?? DisplayName(traderId),
                        traderMultiplier));
                }
                break;

            case "flea-availability":
                if (RequiredBoolean(effect, "enabled", path, issues, out var enabled))
                {
                    parsed.Add(new FleaAvailabilityRule(enabled));
                }
                break;

            case "map-availability":
                if (RequiredString(effect, "mapId", path, issues, out var mapId) &&
                    RequiredBoolean(effect, "available", path, issues, out var available))
                {
                    parsed.Add(new MapAvailabilityRule(
                        mapId,
                        OptionalString(effect, "mapName", path, issues) ?? DisplayName(mapId),
                        available));
                }
                break;

            case "boss-spawn-multiplier":
                if (RequiredString(effect, "bossId", path, issues, out var bossId) &&
                    PositiveMultiplier(effect, "multiplier", path, issues, out var bossMultiplier))
                {
                    parsed.Add(new BossSpawnMultiplierRule(
                        bossId,
                        OptionalString(effect, "bossName", path, issues) ?? DisplayName(bossId),
                        OptionalString(effect, "mapId", path, issues),
                        bossMultiplier));
                }
                break;

            case "quest-availability-window":
                ParseQuestWindow(effect, path, parsed, issues);
                break;

            default:
                issues.Add(new($"{path}.type", $"unknown effect type '{type}'"));
                break;
        }
    }

    private static void ParseQuestWindow(
        JsonElement effect,
        string path,
        List<EventRuleEffect> parsed,
        List<EventRuleValidationIssue> issues)
    {
        if (!RequiredString(effect, "questId", path, issues, out var questId))
        {
            return;
        }

        var startRead = OptionalInstant(effect, "startUtc", path, issues, out var start);
        var endRead = OptionalInstant(effect, "endUtc", path, issues, out var end);
        if (!startRead || !endRead)
        {
            return;
        }

        if (start is null && end is null)
        {
            issues.Add(new(path, "requires startUtc, endUtc, or both"));
            return;
        }

        if (start > end)
        {
            issues.Add(new(path, "endUtc cannot be before startUtc"));
            return;
        }

        parsed.Add(new QuestAvailabilityWindowRule(
            questId,
            OptionalString(effect, "questName", path, issues) ?? DisplayName(questId),
            start,
            end));
    }

    private static bool RequiredString(
        JsonElement element,
        string name,
        string path,
        List<EventRuleValidationIssue> issues,
        out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            issues.Add(new($"{path}.{name}", "is required and must be a non-empty string"));
            return false;
        }

        value = property.GetString()!.Trim();
        if (value.Length > MaximumTextLength)
        {
            issues.Add(new($"{path}.{name}", $"cannot exceed {MaximumTextLength} characters"));
            value = string.Empty;
            return false;
        }

        return true;
    }

    private static string? OptionalString(
        JsonElement element,
        string name,
        string path,
        List<EventRuleValidationIssue> issues)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            issues.Add(new($"{path}.{name}", "must be a string when provided"));
            return null;
        }

        var value = NullIfWhiteSpace(property.GetString());
        if (value?.Length > MaximumTextLength)
        {
            issues.Add(new($"{path}.{name}", $"cannot exceed {MaximumTextLength} characters"));
            return null;
        }

        return value;
    }

    private static bool RequiredBoolean(
        JsonElement element,
        string name,
        string path,
        List<EventRuleValidationIssue> issues,
        out bool value)
    {
        value = false;
        if (!element.TryGetProperty(name, out var property) ||
            property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            issues.Add(new($"{path}.{name}", "is required and must be true or false"));
            return false;
        }

        value = property.GetBoolean();
        return true;
    }

    private static bool PositiveMultiplier(
        JsonElement element,
        string name,
        string path,
        List<EventRuleValidationIssue> issues,
        out decimal value)
    {
        value = 0;
        if (!element.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.Number ||
            !property.TryGetDecimal(out value) ||
            value <= 0 || value > 100)
        {
            issues.Add(new($"{path}.{name}", "is required and must be greater than 0 and at most 100"));
            return false;
        }

        return true;
    }

    private static bool OptionalInstant(
        JsonElement element,
        string name,
        string path,
        List<EventRuleValidationIssue> issues,
        out DateTimeOffset? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        var text = property.ValueKind == JsonValueKind.String ? property.GetString() : null;
        var hasOffset = text is not null &&
            (text.EndsWith('Z') ||
             (text.Length >= 6 && text[^6] is '+' or '-' && text[^3] == ':'));
        if (!hasOffset ||
            !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            issues.Add(new($"{path}.{name}", "must be an ISO 8601 timestamp with an offset"));
            return false;
        }

        value = parsed.ToUniversalTime();
        return true;
    }

    private static string DisplayName(string id)
    {
        var words = id.Replace('-', ' ').Replace('_', ' ').Trim();
        return words.Length == 0 ? id : char.ToUpperInvariant(words[0]) + words[1..];
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Reads and evaluates local event rules once for a recommendation or Plan refresh.</summary>
public sealed class EventRuleService(IEventCatalog catalog, TimeProvider? timeProvider = null)
{
    private readonly IEventCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public Task<EventRuleEvaluation> ReadActiveAsync(CancellationToken cancellationToken) =>
        ReadActiveAsync(_timeProvider.GetUtcNow(), cancellationToken);

    public async Task<EventRuleEvaluation> ReadActiveAsync(
        DateTimeOffset evaluatedUtc,
        CancellationToken cancellationToken)
    {
        var definitions = await _catalog.GetAsync(cancellationToken).ConfigureAwait(false);
        return Evaluate(definitions, evaluatedUtc);
    }

    public static EventRuleEvaluation Evaluate(
        IEnumerable<EventDefinition> definitions,
        DateTimeOffset evaluatedUtc)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var active = new List<ActiveEventRule>();
        var invalid = new Dictionary<string, IReadOnlyList<EventRuleValidationIssue>>(StringComparer.Ordinal);
        foreach (var definition in definitions.OrderBy(definition => definition.Id, StringComparer.Ordinal))
        {
            var parsed = EventRuleParser.Parse(definition.RulesJson);
            if (!parsed.IsValid)
            {
                invalid[definition.Id] = parsed.Issues;
                continue;
            }

            if (!IsActive(definition, evaluatedUtc))
            {
                continue;
            }

            active.AddRange(parsed.Rules.Effects.Select(effect => new ActiveEventRule(
                definition.Id,
                definition.Name,
                effect,
                definition.Provenance)));
        }

        return new(new ActiveEventRules(active.AsReadOnly()), invalid);
    }

    public static bool IsActive(EventDefinition definition, DateTimeOffset atUtc) =>
        definition.Active &&
        (definition.StartUtc is not { } start || start <= atUtc) &&
        (definition.EndUtc is not { } end || end >= atUtc);
}

public static class EventRulePriceAdjustment
{
    public static ItemPriceSnapshot Apply(ItemPriceSnapshot price, ActiveEventRules rules)
    {
        ArgumentNullException.ThrowIfNull(price);
        ArgumentNullException.ThrowIfNull(rules);
        var offers = price.TraderOffers.Select(offer => offer with
        {
            ValueRoubles = Scale(offer.ValueRoubles, rules.TraderMultiplier(offer.TraderId)),
        }).ToArray();
        return price with
        {
            FleaPriceRoubles = rules.FleaEnabled ? price.FleaPriceRoubles : null,
            Average24HourRoubles = rules.FleaEnabled ? price.Average24HourRoubles : null,
            Low24HourRoubles = rules.FleaEnabled ? price.Low24HourRoubles : null,
            High24HourRoubles = rules.FleaEnabled ? price.High24HourRoubles : null,
            TraderOffers = offers,
        };
    }

    private static long Scale(long value, decimal multiplier) =>
        (long)Math.Min(
            long.MaxValue,
            Math.Round(value * multiplier, MidpointRounding.AwayFromZero));
}
