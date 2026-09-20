using System.Net;

namespace TarkovCompanion.Application.Services.Devices;

/// <summary>
/// Whether a configured relay address is one a paired tablet's device-key proof can be pinned to:
/// an exact HTTPS DNS origin, no path, no IP address (the production
/// <c>WebAuthnDeviceKeyProofVerifier</c>'s own requirement).
/// </summary>
/// <remarks>
/// Shared so the value <c>AppComposition</c> reads once at startup from <c>group.json</c> and any
/// live reconfiguration afterward — <see cref="RelayMarksBridge.Configure"/> could always be
/// called again, but nothing called it after the first read — can never validate the same string
/// two different ways.
/// </remarks>
public static class CompanionRelayOrigin
{
    public static Uri? TryParse(string? serverUri)
    {
        if (string.IsNullOrWhiteSpace(serverUri) ||
            !Uri.TryCreate(serverUri, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            IPAddress.TryParse(uri.IdnHost, out _))
        {
            return null;
        }

        return new Uri(uri.GetLeftPart(UriPartial.Authority));
    }
}
