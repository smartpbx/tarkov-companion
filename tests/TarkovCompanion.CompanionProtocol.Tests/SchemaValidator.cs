using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TarkovCompanion.CompanionProtocol.Tests;

/// <summary>
/// A deliberately small JSON Schema 2020-12 evaluator for the keyword subset the paired protocol
/// schema uses. An unsupported keyword is itself an error, so the schema cannot quietly depend on
/// behavior this evidence does not check. It avoids adding a third-party schema dependency.
/// </summary>
internal sealed class SchemaValidator(JsonNode schema)
{
    private static readonly HashSet<string> Annotations =
        ["$schema", "$id", "title", "$comment", "description", "format", "$defs"];

    private static readonly HashSet<string> Assertions =
    [
        "$ref", "type", "required", "properties", "enum", "const", "minimum", "maximum", "minLength", "maxLength",
        "pattern", "items", "minItems", "maxItems", "oneOf", "anyOf", "allOf", "not", "if", "then", "else",
    ];

    public IReadOnlyList<string> Validate(JsonNode? instance) => Validate(schema, instance, "$", strict: false);

    public IReadOnlyList<string> ValidateDefinition(string definition, JsonNode? instance) =>
        Validate(Definition(definition), instance, "$", strict: false);

    /// <summary>
    /// Also reports any object member the matching schema shape does not declare, which proves the
    /// schema documents every field the serializer actually writes.
    /// </summary>
    public IReadOnlyList<string> ValidateStrict(string definition, JsonNode? instance) =>
        Validate(Definition(definition), instance, "$", strict: true);

    public JsonNode Definition(string name) =>
        schema["$defs"]?[name] ?? throw new KeyNotFoundException($"Schema definition '{name}' does not exist.");

    public IEnumerable<string> UnresolvedReferences() => References(schema)
        .Where(reference => !reference.StartsWith("#/$defs/", StringComparison.Ordinal) ||
                            schema["$defs"]?[reference["#/$defs/".Length..]] is null);

    private static IEnumerable<string> References(JsonNode? node) => node switch
    {
        JsonObject obj => obj
            .SelectMany(property => property.Key == "$ref" && property.Value is JsonValue value
                ? new[] { value.GetValue<string>() }
                : References(property.Value)),
        JsonArray array => array.SelectMany(item => References(item)),
        _ => Enumerable.Empty<string>(),
    };

    private List<string> Validate(JsonNode schemaNode, JsonNode? instance, string path, bool strict)
    {
        var errors = new List<string>();
        var rule = schemaNode.AsObject();
        foreach (var keyword in rule.Select(property => property.Key))
        {
            if (!Annotations.Contains(keyword) && !Assertions.Contains(keyword))
            {
                errors.Add($"{path}: unsupported schema keyword '{keyword}'");
            }
        }

        if (rule["$ref"] is JsonValue reference)
        {
            var name = reference.GetValue<string>()["#/$defs/".Length..];
            errors.AddRange(Validate(Definition(name), instance, path, strict));
        }

        if (rule["type"] is { } typeNode)
        {
            var allowed = typeNode is JsonArray types
                ? types.Select(item => item!.GetValue<string>()).ToArray()
                : new[] { typeNode.GetValue<string>() };
            var actual = TypeOf(instance);
            if (!allowed.Contains(actual) && !(actual == "integer" && allowed.Contains("number")))
            {
                errors.Add($"{path}: type {actual} is not {string.Join('|', allowed)}");
            }
        }

        if (rule["enum"] is JsonArray choices && !choices.Any(choice => JsonNode.DeepEquals(choice, instance)))
        {
            errors.Add($"{path}: value is not in the enum");
        }

        if (rule.ContainsKey("const") && !JsonNode.DeepEquals(rule["const"], instance))
        {
            errors.Add($"{path}: value does not equal const");
        }

        if (instance is JsonValue scalar)
        {
            if (TypeOf(instance) is "integer" or "number")
            {
                var number = scalar.GetValue<double>();
                if (rule["minimum"] is { } minimum && number < minimum.GetValue<double>())
                {
                    errors.Add($"{path}: below minimum");
                }

                if (rule["maximum"] is { } maximum && number > maximum.GetValue<double>())
                {
                    errors.Add($"{path}: above maximum");
                }
            }

            if (TypeOf(instance) == "string")
            {
                var text = scalar.GetValue<string>();
                if (rule["minLength"] is { } minLength && text.Length < minLength.GetValue<int>())
                {
                    errors.Add($"{path}: shorter than minLength");
                }

                if (rule["maxLength"] is { } maxLength && text.Length > maxLength.GetValue<int>())
                {
                    errors.Add($"{path}: longer than maxLength");
                }

                if (rule["pattern"] is { } pattern && !Regex.IsMatch(text, pattern.GetValue<string>(), RegexOptions.CultureInvariant))
                {
                    errors.Add($"{path}: does not match pattern");
                }
            }
        }

        if (instance is JsonArray items)
        {
            if (rule["minItems"] is { } minItems && items.Count < minItems.GetValue<int>())
            {
                errors.Add($"{path}: fewer than minItems");
            }

            if (rule["maxItems"] is { } maxItems && items.Count > maxItems.GetValue<int>())
            {
                errors.Add($"{path}: more than maxItems");
            }

            if (rule["items"] is { } itemSchema)
            {
                for (var index = 0; index < items.Count; index++)
                {
                    errors.AddRange(Validate(itemSchema, items[index], $"{path}[{index}]", strict));
                }
            }
        }

        if (instance is JsonObject obj)
        {
            if (rule["required"] is JsonArray required)
            {
                errors.AddRange(required
                    .Select(item => item!.GetValue<string>())
                    .Where(name => !obj.ContainsKey(name))
                    .Select(name => $"{path}: missing required '{name}'"));
            }

            if (rule["properties"] is JsonObject properties)
            {
                foreach (var property in properties)
                {
                    if (obj.TryGetPropertyValue(property.Key, out var value))
                    {
                        errors.AddRange(Validate(property.Value!, value, $"{path}.{property.Key}", strict));
                    }
                }

                if (strict)
                {
                    var declared = DeclaredProperties(rule);
                    errors.AddRange(obj
                        .Select(member => member.Key)
                        .Where(key => !declared.Contains(key))
                        .Select(key => $"{path}: member '{key}' is not declared by the schema"));
                }
            }
        }

        if (rule["allOf"] is JsonArray allOf)
        {
            foreach (var branch in allOf)
            {
                errors.AddRange(Validate(branch!, instance, path, strict: false));
            }
        }

        // The strict extension checks undeclared members against the enclosing object shape. An
        // anyOf branch is often only a condition over one of those members, so applying strictness
        // again inside it would incorrectly require that condition to redeclare every sibling.
        if (rule["anyOf"] is JsonArray anyOf &&
            !anyOf.Any(branch => Validate(branch!, instance, path, strict: false).Count == 0))
        {
            errors.Add($"{path}: no anyOf branch matched");
        }

        if (rule["oneOf"] is JsonArray oneOf)
        {
            var matches = oneOf.Count(branch => Validate(branch!, instance, path, strict).Count == 0);
            if (matches != 1)
            {
                errors.Add($"{path}: oneOf matched {matches} branches");
            }
        }

        if (rule["not"] is { } negated && Validate(negated, instance, path, strict: false).Count == 0)
        {
            errors.Add($"{path}: matched a not schema");
        }

        if (rule["if"] is { } condition)
        {
            var branch = Validate(condition, instance, path, strict: false).Count == 0 ? rule["then"] : rule["else"];
            if (branch is not null)
            {
                errors.AddRange(Validate(branch, instance, path, strict: false));
            }
        }

        return errors;
    }

    private HashSet<string> DeclaredProperties(JsonObject rule)
    {
        var declared = new HashSet<string>(StringComparer.Ordinal);
        if (rule["properties"] is JsonObject own)
        {
            declared.UnionWith(own.Select(property => property.Key));
        }

        if (rule["$ref"] is JsonValue reference)
        {
            declared.UnionWith(DeclaredProperties(Definition(reference.GetValue<string>()["#/$defs/".Length..]).AsObject()));
        }

        if (rule["allOf"] is JsonArray allOf)
        {
            foreach (var branch in allOf)
            {
                declared.UnionWith(DeclaredProperties(branch!.AsObject()));
            }
        }

        return declared;
    }

    private static string TypeOf(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject => "object",
        JsonArray => "array",
        JsonValue value when value.TryGetValue<bool>(out _) => "boolean",
        JsonValue value when value.TryGetValue<string>(out _) => "string",
        JsonValue value when value.TryGetValue<long>(out _) => "integer",
        JsonValue value when value.TryGetValue<double>(out var number) => Math.Floor(number) == number && !double.IsInfinity(number) ? "integer" : "number",
        _ => "unknown",
    };
}
