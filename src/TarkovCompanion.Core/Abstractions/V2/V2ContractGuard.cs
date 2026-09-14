using TarkovCompanion.Core.Domain.Evidence;

namespace TarkovCompanion.Core.Abstractions.V2;

/// <summary>
/// Construction checks shared by v2 records. A payload field that arrives null has lost its
/// evidence, and a list kept by reference can change after validation, so both fail here.
/// </summary>
internal static class V2ContractGuard
{
    public static T NotNull<T>(T? value, string parameterName)
        where T : class =>
        value ?? throw new ArgumentNullException(parameterName);

    public static IReadOnlyList<T> List<T>(IReadOnlyList<T>? values, string parameterName) =>
        EvidenceGuard.ReadOnly(values, parameterName);

    public static string Required(string value, string parameterName) =>
        EvidenceGuard.Required(value, parameterName);

    public static string? Optional(string? value) => EvidenceGuard.TrimOptional(value);

    public static DateTimeOffset Utc(DateTimeOffset value, string parameterName) =>
        EvidenceGuard.Utc(value, parameterName);

    public static DateTimeOffset? UtcOptional(DateTimeOffset? value, string parameterName) =>
        EvidenceGuard.UtcOptional(value, parameterName);

    public static TEnum Defined<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum =>
        EvidenceGuard.Defined(value, parameterName);

    public static TEnum? DefinedOptional<TEnum>(TEnum? value, string parameterName)
        where TEnum : struct, Enum =>
        value is { } present ? EvidenceGuard.Defined(present, parameterName) : null;

    /// <summary>Checks the value, every candidate, and every correction against a lower bound.</summary>
    public static EvidencedValue<int?> AtLeast(EvidencedValue<int?> field, int minimum, string parameterName)
    {
        NotNull(field, parameterName);
        if (Values(field).Any(value => value < minimum))
        {
            throw new ArgumentOutOfRangeException(parameterName, $"{field.FieldId} must be at least {minimum}.");
        }

        return field;
    }

    public static EvidencedValue<long?> AtLeast(EvidencedValue<long?> field, long minimum, string parameterName)
    {
        NotNull(field, parameterName);
        if (Values(field).Any(value => value < minimum))
        {
            throw new ArgumentOutOfRangeException(parameterName, $"{field.FieldId} must be at least {minimum}.");
        }

        return field;
    }

    public static EvidencedValue<TEnum?> Defined<TEnum>(EvidencedValue<TEnum?> field, string parameterName)
        where TEnum : struct, Enum
    {
        NotNull(field, parameterName);
        foreach (var value in Values(field))
        {
            if (value is { } present)
            {
                EvidenceGuard.Defined(present, parameterName);
            }
        }

        return field;
    }

    private static IEnumerable<T?> Values<T>(EvidencedValue<T?> field)
        where T : struct =>
        new[] { field.Value }
            .Concat(field.Candidates.Select(candidate => candidate.Value))
            .Concat(field.Corrections.SelectMany(correction => new[] { correction.OriginalValue, correction.CorrectedValue }));

    // A default identifier struct skips its constructor, including a missing JSON field under
    // non-canonical options, so records that hold one check it again.
    public static CaptureSessionId Defined(CaptureSessionId value, string parameterName) =>
        value.Value == Guid.Empty ? throw new ArgumentException("A capture session id is required.", parameterName) : value;

    public static WorkspaceId Defined(WorkspaceId value, string parameterName) =>
        value.Value == Guid.Empty ? throw new ArgumentException("A workspace id is required.", parameterName) : value;

    public static CompanionDeviceId Defined(CompanionDeviceId value, string parameterName) =>
        value.Value == Guid.Empty ? throw new ArgumentException("A device id is required.", parameterName) : value;

    public static StateChangeId Defined(StateChangeId value, string parameterName) =>
        value.Value == Guid.Empty ? throw new ArgumentException("A change id is required.", parameterName) : value;

    public static StateStreamId Defined(StateStreamId value, string parameterName) =>
        string.IsNullOrWhiteSpace(value.Value) ? throw new ArgumentException("A stream id is required.", parameterName) : value;

    public static StateRevision Positive(StateRevision value, string parameterName) =>
        value.Value < 1 ? throw new ArgumentOutOfRangeException(parameterName, "A change revision starts at one.") : value;

    public static V2ContractVersion Defined(V2ContractVersion value, string parameterName) =>
        value.IsDefined ? value : throw new ArgumentException("A contract version is required.", parameterName);
}
