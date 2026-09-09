using System.Text.Json;
using System.Text.Json.Nodes;

namespace TarkovCompanion.Application.Services;

public sealed class DataTranslationService
{
    public string Apply(string envelopeJson, string translationEnvelopeJson)
    {
        var envelope = JsonNode.Parse(envelopeJson) as JsonObject
            ?? throw new JsonException("The data envelope must be a JSON object.");
        if (!envelope.ContainsKey("data"))
        {
            throw new JsonException("The data envelope is missing the required 'data' property.");
        }

        var translationEnvelope = JsonNode.Parse(translationEnvelopeJson) as JsonObject
            ?? throw new JsonException("The translation envelope must be a JSON object.");
        var translations = translationEnvelope["data"] as JsonObject
            ?? throw new JsonException("The translation envelope is missing the required 'data' object.");

        if (envelope["translations"] is not JsonArray paths)
        {
            return envelope.ToJsonString();
        }

        foreach (var pathNode in paths)
        {
            var path = pathNode?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            ApplyPath(envelope, ParsePath(path), 0, translations);
        }

        return envelope.ToJsonString();
    }

    private static void ApplyPath(JsonNode? node, IReadOnlyList<PathSegment> path, int index, JsonObject translations)
    {
        if (node is null)
        {
            return;
        }

        if (index == path.Count)
        {
            return;
        }

        var segment = path[index];
        if (segment.Kind == PathSegmentKind.Property && node is JsonObject objectNode)
        {
            if (!objectNode.TryGetPropertyValue(segment.Name!, out var child) || child is null)
            {
                return;
            }

            if (index == path.Count - 1)
            {
                objectNode[segment.Name!] = TranslateValue(child, translations);
                return;
            }

            ApplyPath(child, path, index + 1, translations);
            return;
        }

        if (segment.Kind == PathSegmentKind.ObjectWildcard && node is JsonObject wildcardObject)
        {
            foreach (var property in wildcardObject.ToArray())
            {
                if (index == path.Count - 1)
                {
                    wildcardObject[property.Key] = TranslateValue(property.Value, translations);
                }
                else
                {
                    ApplyPath(property.Value, path, index + 1, translations);
                }
            }

            return;
        }

        if (segment.Kind == PathSegmentKind.ArrayWildcard && node is JsonArray array)
        {
            for (var itemIndex = 0; itemIndex < array.Count; itemIndex++)
            {
                if (index == path.Count - 1)
                {
                    array[itemIndex] = TranslateValue(array[itemIndex], translations);
                }
                else
                {
                    ApplyPath(array[itemIndex], path, index + 1, translations);
                }
            }
        }
    }

    private static JsonNode? TranslateValue(JsonNode? node, JsonObject translations)
    {
        if (node is not JsonValue value || !value.TryGetValue<string>(out var translationKey))
        {
            return node;
        }

        return translations.TryGetPropertyValue(translationKey, out var translated) &&
            translated is JsonValue translatedValue &&
            translatedValue.TryGetValue<string>(out var text)
                ? JsonValue.Create(text)
                : node;
    }

    private static IReadOnlyList<PathSegment> ParsePath(string path)
    {
        if (!path.StartsWith("$.", StringComparison.Ordinal))
        {
            throw new JsonException($"Unsupported translation path '{path}'.");
        }

        var segments = new List<PathSegment>();
        foreach (var token in path[2..].Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token == "*")
            {
                segments.Add(new(PathSegmentKind.ObjectWildcard));
            }
            else if (token == "[*]")
            {
                segments.Add(new(PathSegmentKind.ArrayWildcard));
            }
            else if (token.EndsWith("[*]", StringComparison.Ordinal))
            {
                segments.Add(new(PathSegmentKind.Property, token[..^3]));
                segments.Add(new(PathSegmentKind.ArrayWildcard));
            }
            else
            {
                segments.Add(new(PathSegmentKind.Property, token));
            }
        }

        return segments;
    }

    private enum PathSegmentKind
    {
        Property,
        ObjectWildcard,
        ArrayWildcard,
    }

    private sealed record PathSegment(PathSegmentKind Kind, string? Name = null);
}
