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

    public static IReadOnlyList<T> List<T>(IReadOnlyList<T>? values, string parameterName)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        var copy = values.ToArray();
        if (copy.Any(value => value is null))
        {
            throw new ArgumentException("A contract list cannot contain null entries.", parameterName);
        }

        return copy;
    }

    public static string Required(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }

    public static DateTimeOffset Utc(DateTimeOffset value, string parameterName) =>
        value == default
            ? throw new ArgumentException("A UTC timestamp is required.", parameterName)
            : value.ToUniversalTime();

    // A default identifier struct skips its constructor, including during deserialization of
    // a missing field, so records that hold one check it again.
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

    public static V2ContractVersion Defined(V2ContractVersion value, string parameterName) =>
        value.Major < 1 ? throw new ArgumentException("A contract version is required.", parameterName) : value;
}
