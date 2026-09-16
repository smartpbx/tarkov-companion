using TarkovCompanion.Application.Services.LootSpawns;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.LootSpawns;

namespace TarkovCompanion.Infrastructure.GameData.LootSpawns;

public sealed record LootSpawnQuarantineEntry(
    DateTimeOffset DetectedUtc,
    LootSpawnSourceDiagnostic Diagnostic);

/// <summary>An atomic process-lifetime publication head for the runtime-neutral import slice.</summary>
/// <remarks>
/// Durable composition can replace this narrow store without changing parsing or publication
/// policy. Both implementations use the replacement policy below. The assignment happens only
/// after the entire bundle validates; malformed, stale, incompatible and superseded attempts are
/// retained as bounded quarantine evidence and cannot clear the prior head.
/// </remarks>
public sealed class AtomicLootSpawnPublicationStore :
    ILootSpawnSourcePublicationStore,
    IReviewedLootSpawnPublicationReplacementStore,
    IDisposable
{
    public const int MaximumQuarantineEntries = 64;

    public const int MaximumReplacementAuditEntries = 64;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<LootSpawnQuarantineEntry> _quarantine = [];
    private readonly List<LootSpawnPublicationReplacementAuditEntry> _replacementAudit = [];
    private readonly TimeProvider _timeProvider;
    private LootSpawnSourceBundle? _lastKnownGood;
    private bool _disposed;

    public AtomicLootSpawnPublicationStore(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

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
                ValidateReplacement(bundle, current, cancellationToken);
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

    public async ValueTask PublishAuthorizedReplacementAsync(
        LootSpawnSourceBundle bundle,
        LootSpawnPublicationReplacementAuthorization authorization,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(authorization);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = _lastKnownGood ?? throw new LootSpawnSourceImportException(
                "publication.replacement-head-missing",
                "An authorized replacement requires an existing publication head.");
            ValidateAuthorizedReplacement(bundle, current, authorization, cancellationToken);
            _replacementAudit.Add(AuditEntry(bundle, current, authorization, UtcNow()));
            if (_replacementAudit.Count > MaximumReplacementAuditEntries)
            {
                _replacementAudit.RemoveRange(
                    0,
                    _replacementAudit.Count - MaximumReplacementAuditEntries);
            }

            _lastKnownGood = bundle;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<LootSpawnPublicationReplacementAuditEntry>> ReadReplacementAuditAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Array.AsReadOnly(_replacementAudit.ToArray());
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

    /// <summary>
    /// Applies the same monotonic-head policy to process and restart-durable publication stores.
    /// </summary>
    internal static void ValidateReplacement(
        LootSpawnSourceBundle bundle,
        LootSpawnSourceBundle current,
        CancellationToken cancellationToken,
        bool allowReviewedEvidenceRemoval = false)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(current);
        cancellationToken.ThrowIfCancellationRequested();
        if (bundle.Identity.SourceClass != current.Identity.SourceClass ||
            !string.Equals(
                bundle.Identity.SourceIdentifier,
                current.Identity.SourceIdentifier,
                StringComparison.Ordinal))
        {
            throw new LootSpawnSourceImportException(
                "publication.source-conflict",
                "A publication store cannot change reviewed loot-spawn source authority.");
        }

        if (bundle.Identity.ImportedUtc < current.Identity.ImportedUtc)
        {
            throw new LootSpawnSourceImportException(
                "publication.import-regression",
                "An older import observation cannot replace the last-known-good publication.");
        }

        if (bundle.Identity.GeneratedUtc < current.Identity.GeneratedUtc)
        {
            throw new LootSpawnSourceImportException(
                "publication.superseded",
                "An older loot-spawn bundle cannot replace the last-known-good publication.");
        }

        if (bundle.Identity.DataThroughUtc < current.Identity.DataThroughUtc)
        {
            throw new LootSpawnSourceImportException(
                "publication.evidence-regression",
                "A loot-spawn bundle with older source evidence cannot replace the last-known-good publication.");
        }

        if (!allowReviewedEvidenceRemoval && CoverageRegresses(bundle, current, cancellationToken))
        {
            throw new LootSpawnSourceImportException(
                "publication.coverage-regression",
                "A loot-spawn bundle cannot silently shrink the last-known-good map or record coverage.");
        }

        if (!allowReviewedEvidenceRemoval && ItemEvidenceRegresses(bundle, current, cancellationToken))
        {
            throw new LootSpawnSourceImportException(
                "publication.item-evidence-regression",
                "Older or missing item evidence cannot replace the last-known-good publication.");
        }

        if (bundle.Identity.GeneratedUtc == current.Identity.GeneratedUtc &&
            !SameSourceGeneration(bundle.Identity, current.Identity))
        {
            throw new LootSpawnSourceImportException(
                "publication.identity-conflict",
                "Two loot-spawn bundles claim the same generation time with different content or source metadata.");
        }

        if (bundle.Identity.GeneratedUtc == current.Identity.GeneratedUtc &&
            bundle.Identity.ImportedUtc == current.Identity.ImportedUtc &&
            !ItemEvidenceEquivalent(bundle, current, cancellationToken))
        {
            throw new LootSpawnSourceImportException(
                "publication.item-evidence-conflict",
                "One import observation cannot publish conflicting item evidence for the same source bundle.");
        }
    }

    internal static void ValidateAuthorizedReplacement(
        LootSpawnSourceBundle bundle,
        LootSpawnSourceBundle current,
        LootSpawnPublicationReplacementAuthorization authorization,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        if (!string.Equals(
                authorization.ExpectedCurrentContentSha256,
                current.Identity.ContentSha256,
                StringComparison.Ordinal))
        {
            throw new LootSpawnSourceImportException(
                "publication.replacement-head-mismatch",
                "The reviewed replacement authorization does not name the current publication head.");
        }

        ValidateReplacement(
            bundle,
            current,
            cancellationToken,
            allowReviewedEvidenceRemoval: true);
    }

    internal static LootSpawnPublicationReplacementAuditEntry AuditEntry(
        LootSpawnSourceBundle bundle,
        LootSpawnSourceBundle current,
        LootSpawnPublicationReplacementAuthorization authorization,
        DateTimeOffset recordedUtc) => new(
        authorization,
        current.Identity.DatasetVersion,
        bundle.Identity.DatasetVersion,
        bundle.Identity.ContentSha256,
        recordedUtc);

    private DateTimeOffset UtcNow()
    {
        var value = _timeProvider.GetUtcNow();
        return value.Offset == TimeSpan.Zero ? value : value.ToUniversalTime();
    }

    private static bool CoverageRegresses(
        LootSpawnSourceBundle candidate,
        LootSpawnSourceBundle current,
        CancellationToken cancellationToken)
    {
        var byMap = candidate.Coverage.ToDictionary(value => value.MapId, StringComparer.Ordinal);
        foreach (var previous in current.Coverage)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!byMap.TryGetValue(previous.MapId, out var next) ||
                next.KnownRecordCount < previous.KnownRecordCount ||
                next.PublishedRecordCount < previous.PublishedRecordCount ||
                next.PositionedRecordCount < previous.PositionedRecordCount ||
                next.FloorResolvedRecordCount < previous.FloorResolvedRecordCount)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ItemEvidenceRegresses(
        LootSpawnSourceBundle candidate,
        LootSpawnSourceBundle current,
        CancellationToken cancellationToken)
    {
        var nextItems = CandidateIndex(candidate, cancellationToken);
        foreach (var (key, previous) in CandidateIndex(current, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!nextItems.TryGetValue(key, out var next) ||
                FieldEvidenceThrough(next.FleaGrossRoubles) < FieldEvidenceThrough(previous.FleaGrossRoubles) ||
                FieldEvidenceThrough(next.FleaNetRoubles) < FieldEvidenceThrough(previous.FleaNetRoubles) ||
                FieldEvidenceThrough(next.BestTraderRoubles) < FieldEvidenceThrough(previous.BestTraderRoubles) ||
                FieldEvidenceThrough(next.OccupiedSquares) < FieldEvidenceThrough(previous.OccupiedSquares))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ItemEvidenceEquivalent(
        LootSpawnSourceBundle candidate,
        LootSpawnSourceBundle current,
        CancellationToken cancellationToken)
    {
        var nextItems = CandidateIndex(candidate, cancellationToken);
        var previousItems = CandidateIndex(current, cancellationToken);
        if (nextItems.Count != previousItems.Count)
        {
            return false;
        }

        foreach (var (key, previous) in previousItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!nextItems.TryGetValue(key, out var next) ||
                !FieldEquivalent(next.FleaGrossRoubles, previous.FleaGrossRoubles) ||
                !FieldEquivalent(next.FleaNetRoubles, previous.FleaNetRoubles) ||
                !FieldEquivalent(next.BestTraderRoubles, previous.BestTraderRoubles) ||
                !FieldEquivalent(next.OccupiedSquares, previous.OccupiedSquares))
            {
                return false;
            }
        }

        return true;
    }

    private static Dictionary<(string MapId, string SpawnId, string ItemId), LootSpawnCandidate> CandidateIndex(
        LootSpawnSourceBundle bundle,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<(string MapId, string SpawnId, string ItemId), LootSpawnCandidate>();
        foreach (var snapshot in bundle.Snapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var record in snapshot.Records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var candidate in record.Candidates)
                {
                    result.Add((snapshot.MapId, record.SpawnId, candidate.ItemId), candidate);
                }
            }
        }

        return result;
    }

    private static DateTimeOffset FieldEvidenceThrough<T>(EvidencedValue<T> field)
    {
        var through = field.Provenance.EvidenceThroughUtc;
        foreach (var candidate in field.Candidates)
        {
            if (candidate.Provenance.EvidenceThroughUtc > through)
            {
                through = candidate.Provenance.EvidenceThroughUtc;
            }
        }

        foreach (var correction in field.Corrections)
        {
            if (correction.CorrectedUtc > through)
            {
                through = correction.CorrectedUtc;
            }
        }

        return through;
    }

    private static bool FieldEquivalent<T>(EvidencedValue<T> candidate, EvidencedValue<T> current) =>
        string.Equals(candidate.FieldId, current.FieldId, StringComparison.Ordinal) &&
        EqualityComparer<T?>.Default.Equals(candidate.Value, current.Value) &&
        candidate.Status == current.Status &&
        candidate.Provenance == current.Provenance &&
        candidate.Bounds == current.Bounds &&
        candidate.Candidates.SequenceEqual(current.Candidates) &&
        candidate.Corrections.SequenceEqual(current.Corrections);

    private static bool SameSourceGeneration(
        LootSpawnSourceIdentity candidate,
        LootSpawnSourceIdentity current) =>
        candidate.SchemaVersion == current.SchemaVersion &&
        string.Equals(candidate.DatasetVersion, current.DatasetVersion, StringComparison.Ordinal) &&
        string.Equals(candidate.ContentSha256, current.ContentSha256, StringComparison.Ordinal) &&
        candidate.GeneratedUtc == current.GeneratedUtc &&
        candidate.DataThroughUtc == current.DataThroughUtc &&
        candidate.SourceClass == current.SourceClass &&
        string.Equals(candidate.SourceIdentifier, current.SourceIdentifier, StringComparison.Ordinal) &&
        string.Equals(candidate.SourceReference, current.SourceReference, StringComparison.Ordinal) &&
        string.Equals(candidate.License, current.License, StringComparison.Ordinal) &&
        candidate.Confidence == current.Confidence &&
        candidate.Producer == current.Producer &&
        candidate.Artifacts.SequenceEqual(current.Artifacts);
}
