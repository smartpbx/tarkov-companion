using System.Buffers;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using TarkovCompanion.CompanionProtocol;

namespace TarkovCompanion.GroupServer.Security;

public enum RelayHttpSurface
{
    Api = 1,
    BrowserApplication,
}

/// <summary>Cookie attributes for the reusable browser session credential.</summary>
public static class RelaySessionCookie
{
    public const string Name = "__Host-TarkovCompanion-Device";

    public static CookieOptions Create(DateTimeOffset expiresUtc) => new()
    {
        Secure = true,
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        IsEssential = true,
        Expires = expiresUtc,
        MaxAge = RelaySecurityBounds.MaximumBrowserSessionLifetime,
    };

    public static CookieOptions Delete() => new()
    {
        Secure = true,
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        IsEssential = true,
        Expires = DateTimeOffset.UnixEpoch,
        MaxAge = TimeSpan.Zero,
    };

    public static string Encode(DeviceSessionId sessionId, string secret)
    {
        if (sessionId.Value == Guid.Empty || !IsSessionSecret(secret))
        {
            throw new ArgumentException("A cookie requires a session id and 256-bit credential.");
        }

        return sessionId.Value.ToString("N") + "." + secret;
    }

    public static bool TryDecode(string? value, out RelayCookieCredential credential)
    {
        credential = default;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
        {
            return false;
        }

        var separator = value.IndexOf('.');
        if (separator != 32 || value.LastIndexOf('.') != separator ||
            !Guid.TryParseExact(value.AsSpan(0, separator), "N", out var sessionId))
        {
            return false;
        }

        var secret = value[(separator + 1)..];
        if (!IsSessionSecret(secret))
        {
            return false;
        }

        credential = new RelayCookieCredential(new DeviceSessionId(sessionId), secret);
        return true;
    }

    private static bool IsSessionSecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64)
        {
            return false;
        }

        try
        {
            return RelayCsrfProtector.DecodeBase64Url(value).Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public readonly record struct RelayCookieCredential(DeviceSessionId SessionId, string Secret);

/// <summary>Applies headers that make every auth and state response non-embeddable and non-cacheable.</summary>
public static class RelaySecurityHeaders
{
    public const string ApiContentSecurityPolicy =
        "default-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'";

    public const string BrowserContentSecurityPolicy =
        "default-src 'self'; base-uri 'none'; object-src 'none'; frame-ancestors 'none'; " +
        "form-action 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; " +
        "connect-src 'self' wss:";

    public const string PermissionsPolicy =
        "accelerometer=(), ambient-light-sensor=(), autoplay=(), camera=(), display-capture=(), " +
        "geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=()";

    public static void Apply(HttpResponse response, RelayHttpSurface surface, bool effectiveHttps)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (!Enum.IsDefined(surface))
        {
            throw new ArgumentOutOfRangeException(nameof(surface));
        }

        response.ContentType = surface == RelayHttpSurface.Api
            ? "application/json; charset=utf-8"
            : "text/html; charset=utf-8";
        response.Headers.ContentSecurityPolicy = surface == RelayHttpSurface.Api
            ? ApiContentSecurityPolicy
            : BrowserContentSecurityPolicy;
        response.Headers.XFrameOptions = "DENY";
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers.ReferrerPolicy = "no-referrer";
        response.Headers.CacheControl = "no-store, max-age=0";
        response.Headers.Pragma = "no-cache";
        response.Headers["Permissions-Policy"] = PermissionsPolicy;
        response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
        response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
        if (effectiveHttps)
        {
            response.Headers.StrictTransportSecurity = "max-age=31536000; includeSubDomains";
        }
    }
}

public sealed record RelayTransportPolicyOptions
{
    public RelayTransportPolicyOptions(
        bool requireHttps,
        bool allowLoopbackHttp,
        IReadOnlyCollection<IPAddress>? trustedForwarders = null)
    {
        RequireHttps = requireHttps;
        AllowLoopbackHttp = allowLoopbackHttp;
        TrustedForwarders = (trustedForwarders ?? []).ToHashSet();
        if (TrustedForwarders.Any(address => address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)))
        {
            throw new ArgumentException("Wildcard addresses cannot be trusted forwarders.", nameof(trustedForwarders));
        }
    }

    public bool RequireHttps { get; }

    public bool AllowLoopbackHttp { get; }

    public IReadOnlySet<IPAddress> TrustedForwarders { get; }
}

public readonly record struct RelayTransportDecision(bool Allowed, bool EffectiveHttps, string Code)
{
    public static RelayTransportDecision Reject { get; } = new(false, false, "transport-rejected");
}

/// <summary>Accepts forwarded transport facts only from explicitly trusted immediate peers.</summary>
/// <remarks>
/// Cloud tunnels and reverse proxies make the socket scheme HTTP even when the public request is
/// HTTPS. Trusting a client-supplied forwarded header would let any caller bypass HTTPS checks, so
/// an untrusted peer carrying any forwarded transport header is rejected rather than ignored.
/// </remarks>
public static class RelayTransportPolicy
{
    private static readonly string[] ForwardedHeaders =
    [
        "Forwarded",
        "X-Forwarded-For",
        "X-Forwarded-Host",
        "X-Forwarded-Proto",
    ];

    public static RelayTransportDecision Evaluate(HttpContext context, RelayTransportPolicyOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);

        var remoteIp = Normalize(context.Connection.RemoteIpAddress);
        var hasForwarded = ForwardedHeaders.Any(header => context.Request.Headers.ContainsKey(header));
        var trustedForwarder = remoteIp is not null && options.TrustedForwarders.Contains(remoteIp);
        if (hasForwarded && !trustedForwarder)
        {
            return RelayTransportDecision.Reject;
        }

        var forwardedProto = context.Request.Headers["X-Forwarded-Proto"].ToString();
        if (trustedForwarder && !string.IsNullOrEmpty(forwardedProto) &&
            (forwardedProto.Contains(',', StringComparison.Ordinal) ||
             !string.Equals(forwardedProto, "https", StringComparison.OrdinalIgnoreCase)))
        {
            return RelayTransportDecision.Reject;
        }

        var effectiveHttps = context.Request.IsHttps ||
            (trustedForwarder && string.Equals(forwardedProto, "https", StringComparison.OrdinalIgnoreCase));
        var loopback = remoteIp is not null && IPAddress.IsLoopback(remoteIp);
        var allowed = !options.RequireHttps || effectiveHttps || (options.AllowLoopbackHttp && loopback && !hasForwarded);
        return allowed
            ? new RelayTransportDecision(true, effectiveHttps, "allowed")
            : RelayTransportDecision.Reject;
    }

    private static IPAddress? Normalize(IPAddress? address) =>
        address?.IsIPv4MappedToIPv6 == true ? address.MapToIPv4() : address;
}

public readonly record struct RelayRequestValidation(bool Allowed, string Code)
{
    public static RelayRequestValidation Accept { get; } = new(true, "allowed");

    public static RelayRequestValidation Reject(string code) => new(false, code);
}

/// <summary>Hostile request validation shared by future pairing and state-sync endpoints.</summary>
public static class RelayRequestGuard
{
    public static RelayRequestValidation ValidateJson(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ContentLength is < 0 or > RelaySecurityBounds.MaximumRequestBytes)
        {
            return RelayRequestValidation.Reject("request-too-large");
        }

        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType) ||
            !string.Equals(contentType.MediaType.Value, "application/json", StringComparison.OrdinalIgnoreCase) ||
            (contentType.Charset.HasValue &&
             !string.Equals(contentType.Charset.Value, "utf-8", StringComparison.OrdinalIgnoreCase)))
        {
            return RelayRequestValidation.Reject("unsupported-content-type");
        }

        return RelayRequestValidation.Accept;
    }

    public static async ValueTask<T> ReadProtocolJsonAsync<T>(
        Stream body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        var rented = ArrayPool<byte>.Shared.Rent(RelaySecurityBounds.MaximumRequestBytes + 1);
        try
        {
            var total = 0;
            while (total <= RelaySecurityBounds.MaximumRequestBytes)
            {
                var read = await body.ReadAsync(rented.AsMemory(total), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            if (total == 0)
            {
                throw new InvalidDataException("request-empty");
            }

            if (total > RelaySecurityBounds.MaximumRequestBytes)
            {
                throw new InvalidDataException("request-too-large");
            }

            return CompanionProtocolJson.Deserialize<T>(rented.AsSpan(0, total));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }
}

/// <summary>A bounded admission gate prevents slow requests from multiplying relay work.</summary>
public sealed class RelayConcurrencyGate : IDisposable
{
    private readonly SemaphoreSlim _gate;
    private readonly TimeSpan _wait;

    public RelayConcurrencyGate(
        int maximum = RelaySecurityBounds.MaximumConcurrentRequests,
        TimeSpan? wait = null)
    {
        if (maximum is < 1 or > RelaySecurityBounds.MaximumConcurrentRequests)
        {
            throw new ArgumentOutOfRangeException(nameof(maximum));
        }

        _wait = wait ?? RelaySecurityBounds.RequestAdmissionTimeout;
        if (_wait <= TimeSpan.Zero || _wait > RelaySecurityBounds.RequestAdmissionTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(wait));
        }

        _gate = new SemaphoreSlim(maximum, maximum);
    }

    public async ValueTask<RelayConcurrencyLease?> TryEnterAsync(CancellationToken cancellationToken = default)
    {
        var entered = await _gate.WaitAsync(_wait, cancellationToken).ConfigureAwait(false);
        return entered ? new RelayConcurrencyLease(_gate) : null;
    }

    public void Dispose() => _gate.Dispose();
}

public sealed class RelayConcurrencyLease : IDisposable
{
    private SemaphoreSlim? _gate;

    internal RelayConcurrencyLease(SemaphoreSlim gate) => _gate = gate;

    public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
}

/// <summary>Public errors contain a stable code and correlation id, never an exception or storage path.</summary>
public sealed record RelayPublicError(string Code, string CorrelationId)
{
    public static RelayPublicError Create(string code, string? traceIdentifier)
    {
        var safeCode = code switch
        {
            "not-authenticated" or "not-authorized" or "csrf-rejected" or "request-too-large" or
            "unsupported-content-type" or "rate-limited" or "conflict" or "expired" or
            "unsupported-version" or "unavailable" => code,
            _ => "request-rejected",
        };
        var correlationId = Guid.TryParse(traceIdentifier, out var parsed)
            ? parsed.ToString("N")
            : Guid.NewGuid().ToString("N");
        return new RelayPublicError(safeCode, correlationId);
    }
}
