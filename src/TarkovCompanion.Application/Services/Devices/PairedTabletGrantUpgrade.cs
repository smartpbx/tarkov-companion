using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.Core.Abstractions.V2;

namespace TarkovCompanion.Application.Services.Devices;

/// <summary>
/// Brings the grants of tablets paired by an older build up to what <see cref="PairedTabletGrant"/>
/// gives a tablet paired today.
/// </summary>
/// <remarks>
/// [#601] #558 added <c>RequestControl</c> to the session grant, but the desktop keeps each paired
/// device and session in its authority store and loads them as they were. A tablet paired before
/// #558 therefore kept a session grant without it: every Control request was refused as
/// unauthorized before the desktop could show Allow, and the only way out was to unpair and pair
/// again.
///
/// Only a <see cref="DeviceAuthorizationRole.Member"/> device is touched, because that is the one
/// role <see cref="PairedTabletGrant"/> describes. Capabilities are only ever added, and only ones
/// that grant lists: a device never ends up with more than a fresh pairing would give it. A session
/// still gets no capability its device lacks.
/// </remarks>
public static class PairedTabletGrantUpgrade
{
    /// <summary>The upgraded state, or the same instance when nothing needed it.</summary>
    public static DesktopCompanionAuthorityState Apply(DesktopCompanionAuthorityState state, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(state);
        var grant = PairedTabletGrant.Create(nowUtc);
        var devices = state.Devices.ToArray();
        var sessions = state.Sessions.ToArray();
        var changed = false;
        var upgradedDevices = new Dictionary<CompanionDeviceId, PairedDevice>();

        for (var index = 0; index < devices.Length; index++)
        {
            var device = devices[index];
            if (device.Role != grant.Role || device.Status != DeviceLifecycleStatus.Active)
            {
                continue;
            }

            var missing = grant.DeviceCapabilities.Except(device.Capabilities).ToArray();
            if (missing.Length > 0)
            {
                device = new PairedDevice(
                    device.DeviceId,
                    device.DisplayName,
                    device.DeviceKey,
                    device.Role,
                    [.. device.Capabilities, .. missing],
                    device.Status,
                    device.CreatedUtc,
                    device.LastUsedUtc,
                    device.LastKeyEpoch,
                    device.ExpiresUtc,
                    device.StatusChangedUtc,
                    device.ReplacedByDeviceId,
                    device.LifecycleReason);
                devices[index] = device;
                changed = true;
            }

            upgradedDevices[device.DeviceId] = device;
        }

        for (var index = 0; index < sessions.Length; index++)
        {
            var session = sessions[index];
            if (session.Status != DeviceSessionStatus.Active ||
                !upgradedDevices.TryGetValue(session.DeviceId, out var device))
            {
                continue;
            }

            var missing = grant.SessionCapabilities
                .Intersect(device.Capabilities)
                .Except(session.Capabilities)
                .ToArray();
            if (missing.Length == 0)
            {
                continue;
            }

            sessions[index] = new DeviceSession(
                session.Establishment,
                session.Status,
                session.Transport,
                session.Surface,
                [.. session.Capabilities, .. missing],
                session.LastUsedUtc,
                session.EndedUtc,
                session.LifecycleReason);
            changed = true;
        }

        return changed ? state.With(devices: devices, sessions: sessions) : state;
    }
}
