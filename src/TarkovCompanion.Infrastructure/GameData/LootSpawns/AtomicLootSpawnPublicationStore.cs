using TarkovCompanion.Application.Services.LootSpawns;

namespace TarkovCompanion.Infrastructure.GameData.LootSpawns;

public sealed record LootSpawnQuarantineEntry(
    DateTimeOffset DetectedUtc,
    LootSpawnSourceDiagnostic Diagnostic);

/// <summary>An atomic process-lifetime publication head for the runtime-neutral import slice.</summary>
/// <remarks>
/// Persistent composition can replace this narrow store without changing parsing or publication
/// policy. The assignment happens only after the entire bundle validates; malformed, stale,
/// incompatible and superseded attempts are retained as bounded quarantine evidence and cannot
/// clear the prior head.
/// </remarks>
public sealed class AtomicLootSpawnPublicationStore : ILootSpawnSourcePublicationStore, IDisposable
{
    public const int MaximumQuarantineEntries = 64;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<LootSpawnQuarantineEntry> _quarantine = [];
    private LootSpawnSourceBundle? _lastKnownGood;
    private bool _disposed;

    public async ValueTask<LootSpawnSourceBundle?> ReadLastKnownGoodAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _lastKnownGood;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask PublishAsync(LootSpawnSourceBundle bundle, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(bundle);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_lastKnownGood is { } current)
            {
                if (bundle.Identity.GeneratedUtc < current.Identity.GeneratedUtc)
                {
                    throw new LootSpawnSourceImportException(
                        "publication.superseded",
                        "An older loot-spawn bundle cannot replace the last-known-good publication.");
                }

                if (bundle.Identity.GeneratedUtc == current.Identity.GeneratedUtc &&
                    !string.Equals(bundle.Identity.ContentSha256, current.Identity.ContentSha256, StringComparison.Ordinal))
                {
                    throw new LootSpawnSourceImportException(
                        "publication.identity-conflict",
                        "Two loot-spawn bundles claim the same generation time with different content.");
                }
            }

            _lastKnownGood = bundle;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask QuarantineAsync(
        LootSpawnSourceDiagnostic diagnostic,
        DateTimeOffset detectedUtc,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(diagnostic);
        if (detectedUtc == default || detectedUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("A non-default UTC quarantine time is required.", nameof(detectedUtc));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _quarantine.Add(new(detectedUtc, diagnostic));
            if (_quarantine.Count > MaximumQuarantineEntries)
            {
                _quarantine.RemoveRange(0, _quarantine.Count - MaximumQuarantineEntries);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<LootSpawnQuarantineEntry>> ReadQuarantineAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Array.AsReadOnly(_quarantine.ToArray());
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
    }
}
