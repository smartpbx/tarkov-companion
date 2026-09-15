using System.Text.Json.Serialization;

namespace TarkovCompanion.Application.Services.Execution;

internal static class RuntimeIdentifier
{
    public const int MaxLength = 128;

    public static string Validate(string value, string parameterName, int maxLength = MaxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var trimmed = value.Trim();
        if (trimmed.Length > maxLength || !trimmed.All(IsSafeCharacter))
        {
            throw new ArgumentException(
                $"{parameterName} must contain at most {maxLength} ASCII letters, digits, '.', ':', '_' or '-'.",
                parameterName);
        }

        return trimmed;
    }

    private static bool IsSafeCharacter(char value) =>
        value is >= 'a' and <= 'z'
        or >= 'A' and <= 'Z'
        or >= '0' and <= '9'
        or '.' or ':' or '_' or '-';
}

public readonly record struct RuntimeFeatureId
{
    public const int MaxLength = 64;

    [JsonConstructor]
    public RuntimeFeatureId(string value) => Value = RuntimeIdentifier.Validate(value, nameof(value), MaxLength);

    public string Value { get; }

    public bool IsDefined => !string.IsNullOrEmpty(Value);

    public override string ToString() => Value;
}

public readonly record struct RuntimeDependencyId
{
    public const int MaxLength = 96;

    [JsonConstructor]
    public RuntimeDependencyId(string value) => Value = RuntimeIdentifier.Validate(value, nameof(value), MaxLength);

    public string Value { get; }

    public bool IsDefined => !string.IsNullOrEmpty(Value);

    public override string ToString() => Value;
}

public readonly record struct OperationScopeId
{
    [JsonConstructor]
    public OperationScopeId(string value) => Value = RuntimeIdentifier.Validate(value, nameof(value));

    public string Value { get; }

    public bool IsDefined => !string.IsNullOrEmpty(Value);

    public override string ToString() => Value;
}

public readonly record struct OperationId
{
    [JsonConstructor]
    public OperationId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("An operation id is required.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public bool IsDefined => Value != Guid.Empty;

    public static OperationId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct CorrelationId
{
    [JsonConstructor]
    public CorrelationId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A correlation id is required.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public bool IsDefined => Value != Guid.Empty;

    public static CorrelationId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}

/// <summary>A process-local latest-operation generation, unrelated to a v2 state revision.</summary>
public readonly record struct OperationGeneration
{
    [JsonConstructor]
    public OperationGeneration(long value)
    {
        if (value < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        Value = value;
    }

    public long Value { get; }

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public readonly record struct IdempotencyKey
{
    [JsonConstructor]
    public IdempotencyKey(string value) => Value = RuntimeIdentifier.Validate(value, nameof(value));

    public string Value { get; }

    public bool IsDefined => !string.IsNullOrEmpty(Value);

    public override string ToString() => Value;
}

public readonly record struct OutboxAggregateId
{
    [JsonConstructor]
    public OutboxAggregateId(string value) => Value = RuntimeIdentifier.Validate(value, nameof(value));

    public string Value { get; }

    public bool IsDefined => !string.IsNullOrEmpty(Value);

    public override string ToString() => Value;
}

public readonly record struct OutboxLeaseToken
{
    [JsonConstructor]
    public OutboxLeaseToken(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A lease token is required.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public bool IsDefined => Value != Guid.Empty;

    public static OutboxLeaseToken New() => new(Guid.NewGuid());
}
