using System.Net;
using System.Security.Cryptography;
using TarkovCompanion.CompanionProtocol;

namespace TarkovCompanion.GroupServer.Security;

public readonly record struct RelayRateDecision(bool Allowed, DateTimeOffset? RetryAfterUtc)
{
    public static RelayRateDecision Permit { get; } = new(true, null);
}

/// <summary>Bounded per-source fixed-window admission for public and authenticated endpoints.</summary>
/// <remarks>
/// Only a digest of the source enters memory or audit correlation. New source partitions are
/// rejected when the partition cap is full, preventing an address spray from turning the rate
/// limiter itself into unbounded storage.
/// </remarks>
public sealed class RelayRateLimiter
{
    private readonly TimeProvider _timeProvider;
    private readonly int _limit;
    private readonly TimeSpan _window;
    private readonly int _maximumPartitions;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Window> _windows = new(StringComparer.Ordinal);

    public RelayRateLimiter(
        TimeProvider timeProvider,
        int limit,
        TimeSpan window,
        int maximumPartitions = RelaySecurityBounds.MaximumRatePartitions)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (limit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        if (window <= TimeSpan.Zero || window > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(window));
        }

        if (maximumPartitions is < 1 or > RelaySecurityBounds.MaximumRatePartitions)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPartitions));
        }

        _timeProvider = timeProvider;
        _limit = limit;
        _window = window;
        _maximumPartitions = maximumPartitions;
    }

    public RelayRateDecision TryConsume(string sourceHash)
    {
        ValidateSourceHash(sourceHash);
        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            RemoveExpired(now);
            if (!_windows.TryGetValue(sourceHash, out var current))
            {
                if (_windows.Count >= _maximumPartitions)
                {
                    var retry = _windows.Values.Min(window => window.StartUtc.Add(_window));
                    return new RelayRateDecision(false, retry);
                }

                _windows[sourceHash] = new Window(now, 1);
                return RelayRateDecision.Permit;
            }

            if (now - current.StartUtc >= _window)
            {
                _windows[sourceHash] = new Window(now, 1);
                return RelayRateDecision.Permit;
            }

            if (current.Count >= _limit)
            {
                return new RelayRateDecision(false, current.StartUtc.Add(_window));
            }

            _windows[sourceHash] = current with { Count = current.Count + 1 };
            return RelayRateDecision.Permit;
        }
    }

    public int PartitionCount
    {
        get
        {
            lock (_gate)
            {
                RemoveExpired(_timeProvider.GetUtcNow());
                return _windows.Count;
            }
        }
    }

    public static string HashSource(ReadOnlySpan<byte> sourceHashKey, IPAddress remoteAddress) =>
        PairedTransportBinding.ComputeSourceHash(sourceHashKey, remoteAddress);

    private static void ValidateSourceHash(string sourceHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceHash);
        byte[] decoded;
        try
        {
            decoded = RelayCsrfProtector.DecodeBase64Url(sourceHash);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("A source hash must be a SHA-256 base64url digest.", nameof(sourceHash), exception);
        }

        if (decoded.Length != SHA256.HashSizeInBytes)
        {
            throw new ArgumentException("A source hash must be a SHA-256 base64url digest.", nameof(sourceHash));
        }
    }

    private void RemoveExpired(DateTimeOffset nowUtc)
    {
        foreach (var key in _windows
                     .Where(pair => nowUtc - pair.Value.StartUtc >= _window)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _windows.Remove(key);
        }
    }

    private sealed record Window(DateTimeOffset StartUtc, int Count);
}
