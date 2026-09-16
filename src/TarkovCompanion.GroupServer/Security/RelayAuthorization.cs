using System.Collections.ObjectModel;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.Core.Abstractions.V2;

namespace TarkovCompanion.GroupServer.Security;

/// <summary>The closed set of operations a relay-authenticated device may request.</summary>
/// <remarks>
/// The relay used to treat knowledge of a room key as authority for every room mutation. These
/// operations are deliberately narrower: the room remains routing context, while a live session
/// bound to a device key decides who may publish, observe, or administer paired traffic.
/// </remarks>
public enum RelayPermission
{
    ReceiveOpaqueFrames = 1,
    PublishOpaqueFrames,
    CreatePairingInvitation,
    ApprovePairingInvitation,
    CloseOwnSession,
    RotateOwnSession,
    RevokeDevice,
    ReplaceDevice,
    ReadSecurityAudit,
}

/// <summary>Identity derived from registry state after a session credential is verified.</summary>
/// <remarks>
/// No display name appears here. Names remain presentation metadata and never participate in an
/// authorization decision or relay attribution.
/// </remarks>
public sealed record RelayPrincipal
{
    public RelayPrincipal(
        CompanionDeviceId deviceId,
        DeviceKeyId deviceKeyId,
        DeviceSessionId sessionId,
        RelayChannelId channelId,
        CompanionProtocolVersion protocolVersion,
        long keyEpoch,
        DeviceAuthorizationRole role,
        IReadOnlyList<DeviceCapability> capabilities,
        CompanionSurfaceKind surface,
        DateTimeOffset authenticatedUtc,
        DateTimeOffset expiresUtc)
    {
        if (deviceId.Value == Guid.Empty || sessionId.Value == Guid.Empty || channelId.Value == Guid.Empty ||
            string.IsNullOrWhiteSpace(deviceKeyId.Value))
        {
            throw new ArgumentException("A relay principal requires device, key, session, and channel identity.");
        }

        if (!protocolVersion.IsDefined || keyEpoch is <= 0 or > ProtocolBounds.MaxKeyEpoch ||
            !Enum.IsDefined(role) || !Enum.IsDefined(surface))
        {
            throw new ArgumentOutOfRangeException(nameof(role));
        }

        ArgumentNullException.ThrowIfNull(capabilities);
        var copied = capabilities.Distinct().ToArray();
        if (copied.Length > 32 || copied.Any(capability => !Enum.IsDefined(capability)))
        {
            throw new ArgumentOutOfRangeException(nameof(capabilities));
        }

        if (!RelayAuthorization.IsCapabilitySetValid(role, copied))
        {
            throw new ArgumentException("The principal capabilities are not valid for its role.", nameof(capabilities));
        }

        if (authenticatedUtc == default || authenticatedUtc.Offset != TimeSpan.Zero ||
            expiresUtc == default || expiresUtc.Offset != TimeSpan.Zero || expiresUtc <= authenticatedUtc ||
            authenticatedUtc.Ticks % TimeSpan.TicksPerMillisecond != 0 ||
            expiresUtc.Ticks % TimeSpan.TicksPerMillisecond != 0)
        {
            throw new ArgumentException("Relay principal timestamps must be ordered millisecond UTC values.");
        }

        DeviceId = deviceId;
        DeviceKeyId = deviceKeyId;
        SessionId = sessionId;
        ChannelId = channelId;
        ProtocolVersion = protocolVersion;
        KeyEpoch = keyEpoch;
        Role = role;
        Capabilities = Array.AsReadOnly(copied);
        Surface = surface;
        AuthenticatedUtc = authenticatedUtc;
        ExpiresUtc = expiresUtc;
    }

    public CompanionDeviceId DeviceId { get; }

    public DeviceKeyId DeviceKeyId { get; }

    public DeviceSessionId SessionId { get; }

    public RelayChannelId ChannelId { get; }

    public CompanionProtocolVersion ProtocolVersion { get; }

    public long KeyEpoch { get; }

    public DeviceAuthorizationRole Role { get; }

    public ReadOnlyCollection<DeviceCapability> Capabilities { get; }

    public CompanionSurfaceKind Surface { get; }

    public DateTimeOffset AuthenticatedUtc { get; }

    public DateTimeOffset ExpiresUtc { get; }
}

public readonly record struct RelayAuthorizationDecision(bool Allowed, string Code)
{
    public static RelayAuthorizationDecision Permit { get; } = new(true, "allowed");

    public static RelayAuthorizationDecision Deny { get; } = new(false, "not-authorized");
}

/// <summary>Central role and capability policy for every relay security mutation.</summary>
public static class RelayAuthorization
{
    // Keep this list explicit. A newly added protocol capability must not silently become an
    // owner grant until the relay policy and threat model have reviewed it.
    private static readonly ReadOnlyCollection<DeviceCapability> OwnerCapabilities = Array.AsReadOnly(
        new[]
        {
            DeviceCapability.FollowDesktop,
            DeviceCapability.RequestControl,
            DeviceCapability.ShowOnDesktop,
            DeviceCapability.ManageOwnMarks,
            DeviceCapability.PublishTeamMarks,
            DeviceCapability.RequestCaptureIntent,
            DeviceCapability.ReviewCaptureResult,
            DeviceCapability.ManageDevices,
            DeviceCapability.ResolveControlRequests,
            DeviceCapability.ReportCaptureProgress,
            DeviceCapability.ManageProfilePreferences,
        });

    private static readonly ReadOnlyCollection<DeviceCapability> MemberCapabilities = Array.AsReadOnly(
        new[]
        {
            DeviceCapability.FollowDesktop,
            DeviceCapability.RequestControl,
            DeviceCapability.ShowOnDesktop,
            DeviceCapability.ManageOwnMarks,
            DeviceCapability.RequestCaptureIntent,
            DeviceCapability.ReviewCaptureResult,
            DeviceCapability.ManageProfilePreferences,
        });

    private static readonly ReadOnlyCollection<DeviceCapability> ObserverCapabilities = Array.AsReadOnly(
        new[] { DeviceCapability.FollowDesktop });

    public static ReadOnlyCollection<DeviceCapability> CapabilitiesFor(DeviceAuthorizationRole role) => role switch
    {
        DeviceAuthorizationRole.Owner => OwnerCapabilities,
        DeviceAuthorizationRole.Member => MemberCapabilities,
        DeviceAuthorizationRole.Observer => ObserverCapabilities,
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    public static bool IsCapabilitySetValid(
        DeviceAuthorizationRole role,
        IEnumerable<DeviceCapability> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        var allowed = CapabilitiesFor(role).ToHashSet();
        return capabilities.All(capability => Enum.IsDefined(capability) && allowed.Contains(capability));
    }

    public static RelayAuthorizationDecision Decide(
        RelayPrincipal? principal,
        RelayPermission permission,
        DateTimeOffset nowUtc,
        CompanionDeviceId? targetDeviceId = null)
    {
        RelayCsrfProtector.ValidateUtc(nowUtc, nameof(nowUtc));
        if (principal is null || !Enum.IsDefined(permission) || nowUtc >= principal.ExpiresUtc ||
            principal.AuthenticatedUtc > nowUtc.Add(ProtocolBounds.MaxClientClockSkew) ||
            !CompanionProtocolVersion.Current.CanRead(principal.ProtocolVersion) ||
            !IsCapabilitySetValid(principal.Role, principal.Capabilities))
        {
            return RelayAuthorizationDecision.Deny;
        }

        var ownsTarget = targetDeviceId is not null && targetDeviceId == principal.DeviceId;
        var allowed = permission switch
        {
            RelayPermission.ReceiveOpaqueFrames => true,
            // The relay cannot see the encrypted payload kind. Observers must be able to send
            // acknowledgements and reconnect traffic; the desktop enforces inner mutations.
            RelayPermission.PublishOpaqueFrames => true,
            RelayPermission.CloseOwnSession or RelayPermission.RotateOwnSession => ownsTarget,
            RelayPermission.CreatePairingInvitation or RelayPermission.ApprovePairingInvitation or
                RelayPermission.RevokeDevice or RelayPermission.ReplaceDevice or RelayPermission.ReadSecurityAudit =>
                principal.Role == DeviceAuthorizationRole.Owner &&
                principal.Capabilities.Contains(DeviceCapability.ManageDevices),
            _ => false,
        };

        return allowed ? RelayAuthorizationDecision.Permit : RelayAuthorizationDecision.Deny;
    }
}
