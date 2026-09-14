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
    public const int MaxLength = 128;

    [JsonConstructor]
    public StateStreamId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Trim().Length > MaxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        Value = value.Trim();
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct StateChangeId
{
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

/// <summary>A stream-local revision. Zero means nothing has been applied; a change starts at one.</summary>
public readonly record struct StateRevision
{
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
    DesktopApplication = 1,
    PairedDevice,
    User,
}

/// <summary>Audit metadata for who made a change. It is not authentication.</summary>
public sealed record WorkspaceOrigin(
    WorkspaceId WorkspaceId,
    CompanionDeviceId DeviceId,
    WorkspaceOriginKind Kind,
    string InstanceId)
{
    public WorkspaceId WorkspaceId { get; } = V2ContractGuard.Defined(WorkspaceId, nameof(WorkspaceId));

    public CompanionDeviceId DeviceId { get; } = V2ContractGuard.Defined(DeviceId, nameof(DeviceId));

    public WorkspaceOriginKind Kind { get; } = V2ContractGuard.Defined(Kind, nameof(Kind));

    public string InstanceId { get; } = V2ContractGuard.Required(InstanceId, nameof(InstanceId));
}

/// <summary>Arms companion context for the next capture; it never reaches or controls EFT.</summary>
public sealed record CaptureIntentState(
    ScanIntent Intent,
    DateTimeOffset ArmedUtc,
    DateTimeOffset? ExpiresUtc) : IWorkspaceStatePayload
{
    public ScanIntent Intent { get; } = V2ContractGuard.Defined(Intent, nameof(Intent));

    public DateTimeOffset ArmedUtc { get; } = V2ContractGuard.Utc(ArmedUtc, nameof(ArmedUtc));

    public DateTimeOffset? ExpiresUtc { get; } =
        V2ContractGuard.UtcOptional(ExpiresUtc, nameof(ExpiresUtc)) is { } expires && expires < ArmedUtc
            ? throw new ArgumentOutOfRangeException(nameof(ExpiresUtc), "An intent cannot expire before it was armed.")
            : ExpiresUtc?.ToUniversalTime();
}

/// <summary>The user's own annotation on a map, in map coordinates. It is never a player position.</summary>
public sealed record MapMarkState(
    string MapId,
    string? FloorId,
    double X,
    double Y,
    string? Label,
    DateTimeOffset? ExpiresUtc) : IWorkspaceStatePayload
{
    public const int MaxLabelLength = 80;

    public string MapId { get; } = V2ContractGuard.Required(MapId, nameof(MapId));

    public string? FloorId { get; } = V2ContractGuard.Optional(FloorId);

    public double X { get; } = double.IsFinite(X) ? X : throw new ArgumentOutOfRangeException(nameof(X));

    public double Y { get; } = double.IsFinite(Y) ? Y : throw new ArgumentOutOfRangeException(nameof(Y));

    public string? Label { get; } = V2ContractGuard.Optional(Label) is { Length: > MaxLabelLength }
        ? throw new ArgumentOutOfRangeException(nameof(Label))
        : V2ContractGuard.Optional(Label);

    public DateTimeOffset? ExpiresUtc { get; } = V2ContractGuard.UtcOptional(ExpiresUtc, nameof(ExpiresUtc));
}

/// <summary>A state revision is monotonic within its named stream, not across the workspace.</summary>
public sealed record RevisionedState<T>
    where T : class, IWorkspaceStatePayload
{
    public RevisionedState(
        StateStreamId streamId,
        StateRevision revision,
        StateChangeId changeId,
        V2ContractVersion contractVersion,
        WorkspaceOrigin origin,
        DateTimeOffset changedUtc,
        T value)
    {
        V2WirePayloads.Require(V2WirePayloads.WorkspaceState, typeof(T), "workspace-state");
        StreamId = V2ContractGuard.Defined(streamId, nameof(streamId));
        Revision = V2ContractGuard.Positive(revision, nameof(revision));
        ChangeId = V2ContractGuard.Defined(changeId, nameof(changeId));
        ContractVersion = V2ContractGuard.Defined(contractVersion, nameof(contractVersion));
        Origin = V2ContractGuard.NotNull(origin, nameof(origin));
        ChangedUtc = V2ContractGuard.Utc(changedUtc, nameof(changedUtc));
        Value = V2ContractGuard.NotNull(value, nameof(value));
    }

    public StateStreamId StreamId { get; }

    public StateRevision Revision { get; }

    public StateChangeId ChangeId { get; }

    public V2ContractVersion ContractVersion { get; }

    public WorkspaceOrigin Origin { get; }

    public DateTimeOffset ChangedUtc { get; }

    public T Value { get; }
}

public enum AcknowledgementDisposition
{
    Applied = 1,
    RejectedStale,
    RejectedConflict,
    UnsupportedVersion,
}

/// <summary>
/// The receiver's answer to one change. <see cref="RequestedRevision"/> is the revision the
/// change carried; <see cref="AppliedRevision"/> is the receiver's stream revision after handling
/// it; <see cref="AppliedChangeId"/> is the identity of whichever change occupies
/// <see cref="AppliedRevision"/> in the receiver's stream, or null when nothing has been applied
/// yet (<see cref="AppliedRevision"/> is zero). Desktop and a paired tablet can compute a change
/// against the same prior revision at the same time, so two different changes can target the same
/// requested revision; comparing <see cref="AppliedChangeId"/> to the acknowledged change's own
/// <see cref="ChangeId"/> is what tells a redelivery of the very change that landed apart from a
/// divergent change that landed there instead — revision numbers alone cannot. Applied means the
/// revisions are equal and the applied change is this one (a first apply or a safe, idempotent
/// duplicate delivery); rejected-conflict at equal revisions means the applied change is a
/// different one — a same-revision divergent change from simultaneous desktop/tablet control;
/// rejected-conflict below the requested revision means the receiver holds an earlier, divergent
/// revision; stale means the receiver already holds a later revision; an unsupported version means
/// the receiver cannot read the change's contract version and left its stream untouched.
/// </summary>
public sealed record StateAcknowledgement
{
    public StateAcknowledgement(
        StateStreamId streamId,
        StateChangeId changeId,
        StateRevision requestedRevision,
        StateRevision appliedRevision,
        StateChangeId? appliedChangeId,
        AcknowledgementDisposition disposition,
        V2ContractVersion requestedContractVersion,
        V2ContractVersion receiverContractVersion,
        WorkspaceOrigin origin,
        DateTimeOffset acknowledgedUtc,
        string? detail = null)
    {
        StreamId = V2ContractGuard.Defined(streamId, nameof(streamId));
        ChangeId = V2ContractGuard.Defined(changeId, nameof(changeId));
        RequestedRevision = V2ContractGuard.Positive(requestedRevision, nameof(requestedRevision));
        AppliedRevision = appliedRevision;
        AppliedChangeId = appliedChangeId is { } appliedChange
            ? V2ContractGuard.Defined(appliedChange, nameof(appliedChangeId))
            : null;
        Disposition = V2ContractGuard.Defined(disposition, nameof(disposition));
        RequestedContractVersion = V2ContractGuard.Defined(requestedContractVersion, nameof(requestedContractVersion));
        ReceiverContractVersion = V2ContractGuard.Defined(receiverContractVersion, nameof(receiverContractVersion));
        Origin = V2ContractGuard.NotNull(origin, nameof(origin));
        AcknowledgedUtc = V2ContractGuard.Utc(acknowledgedUtc, nameof(acknowledgedUtc));
        Detail = V2ContractGuard.Optional(detail);

        // Zero means no applied state (see StateRevision): the two either both hold or both fail,
        // never one without the other, regardless of how lax the caller's JSON options are.
        if ((AppliedRevision.Value == 0) != (AppliedChangeId is null))
        {
            throw new ArgumentException(
                "Applied revision zero must carry no applied change id, and a nonzero applied " +
                "revision must name the change that occupies it.",
                nameof(appliedChangeId));
        }

        // A change creates exactly one revision, its requested one. So this change can occupy the
        // applied revision only when the two are equal and it was applied; a stale, conflicting,
        // or unreadable change that names itself as the applied change is claiming a revision it
        // never targeted, and was accepted for rejected-stale until the #264 integration review.
        var readable = receiverContractVersion.CanRead(requestedContractVersion);
        var thisChangeApplied = AppliedChangeId == changeId;
        var consistent = disposition switch
        {
            AcknowledgementDisposition.Applied =>
                readable && appliedRevision == requestedRevision && thisChangeApplied,
            AcknowledgementDisposition.RejectedStale =>
                readable && !thisChangeApplied && appliedRevision.Value > requestedRevision.Value,
            AcknowledgementDisposition.RejectedConflict =>
                readable && !thisChangeApplied && appliedRevision.Value > 0 &&
                appliedRevision.Value <= requestedRevision.Value,
            AcknowledgementDisposition.UnsupportedVersion => !readable && !thisChangeApplied,
            _ => false,
        };

        if (!consistent)
        {
            throw new ArgumentException(
                $"{disposition} is inconsistent with revisions {requestedRevision.Value}/{appliedRevision.Value}, " +
                $"change ids {changeId.Value}/{AppliedChangeId?.Value}, " +
                $"and versions {requestedContractVersion}/{receiverContractVersion}.",
                nameof(disposition));
        }
    }

    public StateStreamId StreamId { get; }

    public StateChangeId ChangeId { get; }

    public StateRevision RequestedRevision { get; }

    public StateRevision AppliedRevision { get; }

    public StateChangeId? AppliedChangeId { get; }

    public AcknowledgementDisposition Disposition { get; }

    public V2ContractVersion RequestedContractVersion { get; }

    public V2ContractVersion ReceiverContractVersion { get; }

    public WorkspaceOrigin Origin { get; }

    public DateTimeOffset AcknowledgedUtc { get; }

    public string? Detail { get; }
}
