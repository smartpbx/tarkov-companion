using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace TarkovCompanion.GroupServer.Security;

/// <summary>
/// Which peers may tell this relay who its caller really is.
/// </summary>
/// <remarks>
/// [#819] The relay is reached through a Cloudflare tunnel whose connector runs on another
/// container, so every request arrives from that connector's address. Every limiter here counts
/// by remote address, so every player on the internet shared one bucket: one stranger guessing
/// keys delayed and then locked out every group at once.
///
/// The fix is the standard one, bounded: `X-Forwarded-For` is honoured only when the immediate
/// peer is a proxy the operator named in <c>TARKOV_RELAY_TRUSTED_PROXIES</c>. From anybody else it
/// is ignored, because a header is written by whoever is nearest and would otherwise let one caller
/// be as many callers as it liked. Only the rightmost entry is taken (ForwardLimit 1): that is the
/// one the trusted proxy appended, and anything to its left came from the client.
///
/// Unset means loopback only, which is the right answer for a relay with a proxy on the same box
/// and changes nothing for one without. A value that does not parse stops the relay starting,
/// because a typo that silently trusted nobody would put the shared bucket back without a word.
/// </remarks>
public static class RelayForwardedHeaders
{
    public const string TrustedProxiesVariable = "TARKOV_RELAY_TRUSTED_PROXIES";

    /// <summary>Builds the forwarded-headers options from a comma- or space-separated list of addresses or CIDR ranges.</summary>
    public static ForwardedHeadersOptions CreateOptions(string? trustedProxies)
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = 1,
        };
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();

        var entries = (trustedProxies ?? string.Empty)
            .Split([',', ' ', ';', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entries.Length == 0)
        {
            options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Loopback, 8));
            options.KnownProxies.Add(IPAddress.IPv6Loopback);
            return options;
        }

        foreach (var entry in entries)
        {
            if (entry.Contains('/', StringComparison.Ordinal))
            {
                if (!System.Net.IPNetwork.TryParse(entry, out var network))
                {
                    throw new FormatException($"{TrustedProxiesVariable}: '{entry}' is not an address or CIDR range.");
                }

                if (network.PrefixLength == 0)
                {
                    throw new FormatException($"{TrustedProxiesVariable}: '{entry}' would trust every caller.");
                }

                options.KnownIPNetworks.Add(network);
            }
            else
            {
                if (!IPAddress.TryParse(entry, out var address))
                {
                    throw new FormatException($"{TrustedProxiesVariable}: '{entry}' is not an address or CIDR range.");
                }

                if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
                {
                    throw new FormatException($"{TrustedProxiesVariable}: '{entry}' would trust every caller.");
                }

                options.KnownProxies.Add(address);
            }
        }

        return options;
    }
}
