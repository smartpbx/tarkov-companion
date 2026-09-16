using System.Security.Cryptography;
using System.Text;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.Core.Abstractions.V2;

namespace TarkovCompanion.GroupServer.Security;

/// <summary>A short-lived one-use recovery assertion created outside the browser session.</summary>
public sealed record OwnerRecoveryGrant(
    CompanionDeviceId NewOwnerDeviceId,
    DeviceKeyId NewOwnerKeyId,
    string NonceBase64Url,
    DateTimeOffset IssuedUtc,
    DateTimeOffset ExpiresUtc,
    string MacBase64Url);

/// <summary>Verifies owner recovery without turning a room key or browser value into a master secret.</summary>
/// <remarks>
/// Composition supplies at least 256 random bits from protected operator configuration. The
/// secret never enters the registry or a browser response. A grant binds the proposed owner
/// device and public-key thumbprint, expires after two minutes, and its nonce is consumed once.
/// </remarks>
public sealed class OwnerRecoveryProtector : IDisposable
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    private const int MaximumConsumedNonces = 256;
    private readonly byte[] _secret;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _consumed = new(StringComparer.Ordinal);
    private bool _disposed;

    public OwnerRecoveryProtector(ReadOnlySpan<byte> secret, TimeProvider timeProvider)
    {
        if (secret.Length < 32)
        {
            throw new ArgumentException("Owner recovery requires at least 256 random bits.", nameof(secret));
        }

        ArgumentNullException.ThrowIfNull(timeProvider);
        _secret = secret.ToArray();
        _timeProvider = timeProvider;
    }

    /// <summary>Used by a trusted local/operator ceremony, never by an unauthenticated endpoint.</summary>
    public OwnerRecoveryGrant CreateGrant(CompanionDeviceId deviceId, DeviceKeyId keyId)
    {
        ThrowIfDisposed();
        ValidateIds(deviceId, keyId);
        var now = MillisecondUtc(_timeProvider.GetUtcNow());
        var nonce = RelayCsrfProtector.Base64Url(RandomNumberGenerator.GetBytes(32));
        var expires = now.Add(Lifetime);
        return new OwnerRecoveryGrant(deviceId, keyId, nonce, now, expires, ComputeMac(deviceId, keyId, nonce, now, expires));
    }

    public bool TryConsume(OwnerRecoveryGrant? grant)
    {
        ThrowIfDisposed();
        if (grant is null)
        {
            return false;
        }

        try
        {
            ValidateIds(grant.NewOwnerDeviceId, grant.NewOwnerKeyId);
            RelayCsrfProtector.ValidateUtc(grant.IssuedUtc, nameof(grant.IssuedUtc));
            RelayCsrfProtector.ValidateUtc(grant.ExpiresUtc, nameof(grant.ExpiresUtc));
            var nonce = RelayCsrfProtector.DecodeBase64Url(grant.NonceBase64Url);
            var presented = RelayCsrfProtector.DecodeBase64Url(grant.MacBase64Url);
            if (nonce.Length != 32 || presented.Length != SHA256.HashSizeInBytes)
            {
                return false;
            }

            var now = MillisecondUtc(_timeProvider.GetUtcNow());
            if (grant.ExpiresUtc <= now || grant.ExpiresUtc - grant.IssuedUtc != Lifetime ||
                grant.IssuedUtc > now.Add(RelaySecurityBounds.ClockSkew))
            {
                return false;
            }

            var expected = RelayCsrfProtector.DecodeBase64Url(ComputeMac(
                grant.NewOwnerDeviceId,
                grant.NewOwnerKeyId,
                grant.NonceBase64Url,
                grant.IssuedUtc,
                grant.ExpiresUtc));
            if (!CryptographicOperations.FixedTimeEquals(expected, presented))
            {
                return false;
            }

            lock (_gate)
            {
                foreach (var expired in _consumed
                             .Where(pair => pair.Value <= now)
                             .Select(pair => pair.Key)
                             .ToArray())
                {
                    _consumed.Remove(expired);
                }

                if (_consumed.ContainsKey(grant.NonceBase64Url) || _consumed.Count >= MaximumConsumedNonces)
                {
                    return false;
                }

                _consumed.Add(grant.NonceBase64Url, grant.ExpiresUtc);
                return true;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or CryptographicException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_secret);
        _disposed = true;
    }

    private string ComputeMac(
        CompanionDeviceId deviceId,
        DeviceKeyId keyId,
        string nonce,
        DateTimeOffset issuedUtc,
        DateTimeOffset expiresUtc)
    {
        var payload = string.Join('\n',
            "tarkov-companion-owner-recovery-v1",
            deviceId.Value.ToString("N"),
            keyId.Value,
            nonce,
            issuedUtc.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            expiresUtc.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        return RelayCsrfProtector.Base64Url(HMACSHA256.HashData(_secret, Encoding.UTF8.GetBytes(payload)));
    }

    private static void ValidateIds(CompanionDeviceId deviceId, DeviceKeyId keyId)
    {
        if (deviceId.Value == Guid.Empty || string.IsNullOrWhiteSpace(keyId.Value))
        {
            throw new ArgumentException("Recovery must bind a device and public key.");
        }
    }

    private static DateTimeOffset MillisecondUtc(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
