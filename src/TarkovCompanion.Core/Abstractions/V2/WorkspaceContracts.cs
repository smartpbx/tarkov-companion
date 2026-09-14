namespace TarkovCompanion.Core.Abstractions.V2;

public readonly record struct WorkspaceId
{
    public WorkspaceId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Workspace id cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }
}

public readonly record struct CompanionDeviceId
{
    public CompanionDeviceId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Device id cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }
}

public readonly record struct StateStreamId
{
    public StateStreamId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value.Trim();
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct StateChangeId
{
    public StateChangeId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Change id cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }
}

public readonly record struct StateRevision
{
    public StateRevision(long value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        Value = value;
    }

    public long Value { get; }

    public StateRevision Next() => new(checked(Value + 1));
}

public enum WorkspaceOriginKind
{
    DesktopApplication,
    PairedDevice,
    User,
}

public sealed record WorkspaceOrigin(
    WorkspaceId WorkspaceId,
    CompanionDeviceId DeviceId,
    WorkspaceOriginKind Kind,
    string InstanceId)
{
    public string InstanceId { get; } = Required(InstanceId, nameof(InstanceId));

    private static string Required(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }
}

/// <summary>A state revision is monotonic within its named stream, not across the workspace.</summary>
public sealed record RevisionedState<T>(
    StateStreamId StreamId,
    StateRevision Revision,
    StateChangeId ChangeId,
    V2ContractVersion ContractVersion,
    WorkspaceOrigin Origin,
    DateTimeOffset ChangedUtc,
    T Value)
{
    public DateTimeOffset ChangedUtc { get; } = RequireUtc(ChangedUtc, nameof(ChangedUtc));

    private static DateTimeOffset RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default)
        {
            throw new ArgumentException("A UTC timestamp is required.", parameterName);
        }

        return value.ToUniversalTime();
    }
}

public enum AcknowledgementDisposition
{
    Applied,
    RejectedStale,
    RejectedConflict,
    UnsupportedVersion,
}

public sealed record StateAcknowledgement(
    StateStreamId StreamId,
    StateChangeId ChangeId,
    StateRevision RequestedRevision,
    StateRevision AppliedRevision,
    AcknowledgementDisposition Disposition,
    WorkspaceOrigin Origin,
    DateTimeOffset AcknowledgedUtc,
    string? Detail = null)
{
    public DateTimeOffset AcknowledgedUtc { get; } = RequireUtc(AcknowledgedUtc, nameof(AcknowledgedUtc));

    private static DateTimeOffset RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default)
        {
            throw new ArgumentException("A UTC timestamp is required.", parameterName);
        }

        return value.ToUniversalTime();
    }
}
