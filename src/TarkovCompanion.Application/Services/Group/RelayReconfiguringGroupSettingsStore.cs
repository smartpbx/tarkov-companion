using TarkovCompanion.Application.Services.Devices;

namespace TarkovCompanion.Application.Services.Group;

/// <summary>
/// Reconfigures the paired-tablet relay bridge the moment the player saves a new group relay
/// address, instead of leaving it on whatever <c>group.json</c> said at startup until the next
/// restart.
/// </summary>
/// <remarks>
/// <see cref="RelayMarksBridge.Configure"/> already handles being called again — naming a
/// different relay than before drops the old one's owner session and routes, and naming the same
/// one is a no-op — but startup was the only caller. This is the second one: the same save the V1
/// Group page already makes, decorated rather than duplicated, so a player who edits the relay
/// address does not have to know that a restart used to be required.
/// </remarks>
public sealed class RelayReconfiguringGroupSettingsStore(
    IGroupSettingsStore inner,
    RelayMarksBridge bridge) : IGroupSettingsStore
{
    public Task<GroupSharingSettings> GetAsync(CancellationToken cancellationToken) =>
        inner.GetAsync(cancellationToken);

    public async Task SaveAsync(GroupSharingSettings settings, CancellationToken cancellationToken)
    {
        await inner.SaveAsync(settings, cancellationToken).ConfigureAwait(false);

        // Turning sharing off, or an address that is not a usable companion relay (http, an IP,
        // one with a path), leaves the bridge on whatever it already had rather than tearing it
        // down — the same thing an unset or unusable group.json already means at startup.
        if (CompanionRelayOrigin.TryParse(settings?.ServerUri) is { } origin)
        {
            bridge.Configure(origin);
        }
    }
}
