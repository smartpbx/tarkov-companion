using System.Text.Json.Serialization;

namespace TarkovCompanion.Core.Abstractions.V2;

public readonly record struct WorkspaceId
{
    // System.Text.Json builds a struct through its implicit parameterless constructor unless told
    // otherwise, which silently round-tripped ids to Guid.Empty and addresses to (0, 0).
    [JsonConstructor]
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
    // System.Text.Json builds a struct through its implicit parameterless constructor unless told
    // otherwise, which silently round-tripped ids to Guid.Empty and addresses to (0, 0).
    [JsonConstructor]
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
    // System.Text.Json builds a struct through its implicit parameterless constructor unless told
    // otherwise, which silently round-tripped ids to Guid.Empty and addresses to (0, 0).
    [JsonConstructor]
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
    // System.Text.Json builds a struct through its implicit parameterless constructor unless told
    // otherwise, which silently round-tripped ids to Guid.Empty and addresses to (0, 0).
    [JsonConstructor]
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
    // System.Text.Json builds a struct through its implicit parameterless constructor unless told
    // otherwise, which silently round-tripped ids to Guid.Empty and addresses to (0, 0).
    [JsonConstructor]
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
    public WorkspaceId WorkspaceId { get; } = V2ContractGuard.Defined(WorkspaceId, nameof(WorkspaceId));

    public CompanionDeviceId DeviceId { get; } = V2ContractGuard.Defined(DeviceId, nameof(DeviceId));

    public string InstanceId { get; } = V2ContractGuard.Required(InstanceId, nameof(InstanceId));
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
    public StateStreamId StreamId { get; } = V2ContractGuard.Defined(StreamId, nameof(StreamId));

    public StateChangeId ChangeId { get; } = V2ContractGuard.Defined(ChangeId, nameof(ChangeId));

    public V2ContractVersion ContractVersion { get; } = V2ContractGuard.Defined(ContractVersion, nameof(ContractVersion));

    public WorkspaceOrigin Origin { get; } = V2ContractGuard.NotNull(Origin, nameof(Origin));

    public DateTimeOffset ChangedUtc { get; } = V2ContractGuard.Utc(ChangedUtc, nameof(ChangedUtc));

    public T Value { get; } = Value is null ? throw new ArgumentNullException(nameof(Value)) : Value;
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
    public StateStreamId StreamId { get; } = V2ContractGuard.Defined(StreamId, nameof(StreamId));

    public StateChangeId ChangeId { get; } = V2ContractGuard.Defined(ChangeId, nameof(ChangeId));

    public WorkspaceOrigin Origin { get; } = V2ContractGuard.NotNull(Origin, nameof(Origin));

    public DateTimeOffset AcknowledgedUtc { get; } = V2ContractGuard.Utc(AcknowledgedUtc, nameof(AcknowledgedUtc));
}
