using System.Security.Cryptography;
using System.Text;
using TarkovCompanion.CompanionProtocol;

namespace TarkovCompanion.GroupServer.Security;

/// <summary>A one-time CSRF token whose reusable session credential stays in an HttpOnly cookie.</summary>
/// <remarks>
/// A double-submit design would require JavaScript to read a cookie. This design returns the
/// anti-CSRF nonce in an authenticated response, keeps it only in page memory, and stores only a
/// digest on the server. Successful validation rotates it, so replaying an earlier mutation does
/// not become a second authorized mutation.
/// </remarks>
public static class RelayCsrfProtector
{
    public const string HeaderName = "X-Tarkov-CSRF";
    public const int TokenBytes = 32;

    public static RelayCsrfToken Issue(DeviceSessionId sessionId, DateTimeOffset nowUtc, TimeSpan lifetime)
    {
        ValidateUtc(nowUtc, nameof(nowUtc));
        if (lifetime <= TimeSpan.Zero || lifetime > RelaySecurityBounds.MaximumBrowserSessionLifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        }

        var secret = RandomNumberGenerator.GetBytes(TokenBytes);
        var token = Base64Url(secret);
        return new RelayCsrfToken(
            token,
            Digest(sessionId, token),
            nowUtc,
            nowUtc.Add(lifetime));
    }

    public static bool Validate(
        DeviceSessionId sessionId,
        string? presentedToken,
        string? expectedDigestBase64Url,
        DateTimeOffset expiresUtc,
        DateTimeOffset nowUtc)
    {
        ValidateUtc(nowUtc, nameof(nowUtc));
        ValidateUtc(expiresUtc, nameof(expiresUtc));
        if (nowUtc >= expiresUtc || string.IsNullOrWhiteSpace(presentedToken) ||
            string.IsNullOrWhiteSpace(expectedDigestBase64Url) ||
            presentedToken.Length > 64 || expectedDigestBase64Url.Length > 64)
        {
            return false;
        }

        byte[] expected;
        byte[] actual;
        try
        {
            if (DecodeBase64Url(presentedToken).Length != TokenBytes)
            {
                return false;
            }

            expected = DecodeBase64Url(expectedDigestBase64Url);
            actual = DecodeBase64Url(Digest(sessionId, presentedToken));
        }
        catch (FormatException)
        {
            return false;
        }

        return expected.Length == SHA256.HashSizeInBytes &&
            actual.Length == SHA256.HashSizeInBytes &&
            CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    public static string Digest(DeviceSessionId sessionId, string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (sessionId.Value == Guid.Empty)
        {
            throw new ArgumentException("A session id is required.", nameof(sessionId));
        }

        var decodedToken = DecodeBase64Url(token);
        if (decodedToken.Length != TokenBytes)
        {
            throw new ArgumentException("A CSRF or session credential is exactly 256 bits.", nameof(token));
        }

        var tokenBytes = Encoding.ASCII.GetBytes(token);
        Span<byte> sessionBytes = stackalloc byte[16];
        sessionId.Value.TryWriteBytes(sessionBytes);
        var material = new byte[sessionBytes.Length + tokenBytes.Length];
        sessionBytes.CopyTo(material);
        tokenBytes.CopyTo(material.AsSpan(sessionBytes.Length));
        return Base64Url(SHA256.HashData(material));
    }

    internal static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static byte[] DecodeBase64Url(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0 || value.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            throw new FormatException("Only unpadded base64url is accepted.");
        }

        var padding = (value.Length % 4) switch
        {
            0 => string.Empty,
            2 => "==",
            3 => "=",
            _ => throw new FormatException("Invalid base64url length."),
        };

        var decoded = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + padding);
        if (!string.Equals(Base64Url(decoded), value, StringComparison.Ordinal))
        {
            throw new FormatException("The base64url value is not canonical.");
        }

        return decoded;
    }

    internal static void ValidateUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero ||
            value.Ticks % TimeSpan.TicksPerMillisecond != 0)
        {
            throw new ArgumentException("An explicit millisecond UTC timestamp is required.", parameterName);
        }
    }
}

public sealed record RelayCsrfToken(
    string Token,
    string DigestBase64Url,
    DateTimeOffset IssuedUtc,
    DateTimeOffset ExpiresUtc);

public static class RelaySecurityBounds
{
    public const int MaximumSessions = 64;
    public const int MaximumSessionsPerDevice = 1;
    public const int MaximumAuditEvents = 2_048;
    public const int MaximumPairingInvitations = 256;
    public const int MaximumRatePartitions = 4_096;
    public const int MaximumConcurrentRequests = 64;
    public const int MaximumChannels = 512;
    public const int MaximumChannelParticipants = 32;
    public const int MaximumQueuedFramesPerParticipant = 64;
    public const int MaximumQueuedBytesPerParticipant = 4 * 1024 * 1024;
    public const int MaximumRegistryBytes = 2 * 1024 * 1024;
    public const int MaximumRequestBytes = ProtocolBounds.MaxPayloadBytes;
    public const int MaximumRelayFrameRequestBytes = ProtocolBounds.MaxRelayFrameBytes;

    public static TimeSpan SessionLifetime { get; } = TimeSpan.FromHours(12);
    public static TimeSpan MaximumBrowserSessionLifetime { get; } = TimeSpan.FromHours(12);
    public static TimeSpan ClockSkew { get; } = ProtocolBounds.MaxClientClockSkew;
    public static TimeSpan RequestAdmissionTimeout { get; } = TimeSpan.FromSeconds(2);
}
