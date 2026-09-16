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
        string expectedDigestBase64Url,
        DateTimeOffset expiresUtc,
        DateTimeOffset nowUtc)
    {
        ValidateUtc(nowUtc, nameof(nowUtc));
        if (nowUtc >= expiresUtc || string.IsNullOrWhiteSpace(presentedToken) ||
            presentedToken.Length > 64 || expectedDigestBase64Url.Length > 64)
        {
            return false;
        }

        byte[] expected;
        byte[] actual;
        try
        {
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
        var padding = value.Length % 4 switch
        {
            0 => string.Empty,
            2 => "==",
            3 => "=",
            _ => throw new FormatException("Invalid base64url length."),
        };

        return Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + padding);
    }

    internal static void ValidateUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("An explicit UTC timestamp is required.", parameterName);
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
    public const int MaximumSessionsPerDevice = 4;
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

    public static TimeSpan SessionLifetime { get; } = TimeSpan.FromHours(12);
    public static TimeSpan MaximumBrowserSessionLifetime { get; } = TimeSpan.FromHours(12);
    public static TimeSpan ClockSkew { get; } = TimeSpan.FromMinutes(2);
    public static TimeSpan RequestAdmissionTimeout { get; } = TimeSpan.FromSeconds(2);
}
