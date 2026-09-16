using System.Text.Json;
using TarkovCompanion.Core.Domain.Strategy.Data;

namespace TarkovCompanion.Infrastructure.Strategy.Datasets;

public sealed record TrafficSnapshotStoreOptions(
    string RootDirectory,
    int MaximumQuarantineReceipts = 128,
    int MaximumRetainedVersions = 4)
{
    public string ValidatedRootDirectory
    {
        get
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(RootDirectory);
            if (MaximumQuarantineReceipts is < 1 or > 4_096)
            {
                throw new ArgumentOutOfRangeException(nameof(MaximumQuarantineReceipts));
            }

            if (MaximumRetainedVersions is < 2 or > 64)
            {
                throw new ArgumentOutOfRangeException(nameof(MaximumRetainedVersions));
            }

            return Path.GetFullPath(RootDirectory);
        }
    }
}

public sealed record TrafficSnapshotInstallResult(
    TrafficModelImportDisposition Disposition,
    string ReasonCode,
    string? CurrentReceiptSha256,
    string? LastKnownGoodReceiptSha256,
    TrafficModelPublication? Publication);

/// <summary>
/// Content-addressed offline snapshot store. A package is validated before staging and one state
/// file move publishes it; refusal writes only a bounded receipt and cannot change either head.
/// </summary>
public sealed class TrafficSnapshotStore : IDisposable
{
    private const int StateSchemaVersion = 1;
    private readonly string _rootDirectory;
    private readonly int _maximumQuarantineReceipts;
    private readonly int _maximumRetainedVersions;
    private readonly TrafficModelPackageImporter _importer;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TrafficSnapshotStore(
        TrafficSnapshotStoreOptions options,
        TrafficModelPackageImporter importer,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _rootDirectory = options.ValidatedRootDirectory;
        _maximumQuarantineReceipts = options.MaximumQuarantineReceipts;
        _maximumRetainedVersions = options.MaximumRetainedVersions;
        _importer = importer ?? throw new ArgumentNullException(nameof(importer));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<TrafficSnapshotInstallResult> InstallAsync(
        TrafficModelPackageStreams package,
        TrafficCompatibilityScope? requiredScope,
        CancellationToken cancellationToken)
    {
        var imported = await _importer.ImportAsync(package, requiredScope, cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_rootDirectory);
            var state = await ReadStateAsync(cancellationToken).ConfigureAwait(false);
            if (!imported.IsAccepted)
            {
                await WriteQuarantineReceiptAsync(imported, cancellationToken).ConfigureAwait(false);
                return Result(imported, state);
            }

            if (state is not null)
            {
                var current = await ImportStoredAsync(state.CurrentReceiptSha256, requiredScope: null, cancellationToken)
                    .ConfigureAwait(false);
                if (current.IsAccepted && HasSameSignedManifest(imported, current))
                {
                    return Result(current, state) with { ReasonCode = "already-current-manifest" };
                }

                foreach (var receipt in new[] { state.CurrentReceiptSha256, state.LastKnownGoodReceiptSha256 }
                             .Distinct(StringComparer.Ordinal))
                {
                    var installed = string.Equals(receipt, state.CurrentReceiptSha256, StringComparison.Ordinal)
                        ? current
                        : await ImportStoredAsync(receipt, requiredScope: null, cancellationToken).ConfigureAwait(false);
                    if (!installed.IsAccepted || DowngradeReason(imported, installed) is not { } reason)
                    {
                        continue;
                    }

                    var refused = RefusedInstall(imported, reason);
                    await WriteQuarantineReceiptAsync(refused, cancellationToken).ConfigureAwait(false);
                    return Result(refused, state);
                }
            }

            var target = VersionDirectory(imported.ReceiptSha256);
            if (Directory.Exists(target))
            {
                var existing = await ImportStoredAsync(imported.ReceiptSha256, requiredScope: null, cancellationToken)
                    .ConfigureAwait(false);
                if (!existing.IsAccepted ||
                    !string.Equals(existing.ReceiptSha256, imported.ReceiptSha256, StringComparison.Ordinal))
                {
                    if (existing.IsAccepted)
                    {
                        existing = new TrafficModelImportResult(
                            TrafficModelImportDisposition.Quarantined,
                            existing.ReceiptSha256,
                            "content-address-mismatch",
                            null,
                            null,
                            null,
                            null,
                            null,
                            null);
                    }

                    await WriteQuarantineReceiptAsync(existing, cancellationToken).ConfigureAwait(false);
                    return Result(existing, state);
                }
            }
            else
            {
                await StageVersionAsync(imported, target, cancellationToken).ConfigureAwait(false);
            }

            var next = state is null
                ? new SnapshotState(StateSchemaVersion, 1, imported.ReceiptSha256, imported.ReceiptSha256)
                : new SnapshotState(
                    StateSchemaVersion,
                    checked(state.Revision + 1),
                    imported.ReceiptSha256,
                    state.CurrentReceiptSha256);
            await WriteStateAsync(next, cancellationToken).ConfigureAwait(false);
            PruneVersions(next);
            return Result(imported, next);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TrafficSnapshotInstallResult> LoadAsync(
        TrafficCompatibilityScope? requiredScope,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await ReadStateAsync(cancellationToken).ConfigureAwait(false);
            if (state is null)
            {
                return new TrafficSnapshotInstallResult(
                    TrafficModelImportDisposition.Incompatible,
                    "no-installed-snapshot",
                    null,
                    null,
                    null);
            }

            var current = await ImportStoredAsync(state.CurrentReceiptSha256, requiredScope, cancellationToken).ConfigureAwait(false);
            if (current.IsAccepted)
            {
                return Result(current, state);
            }

            if (current.Disposition == TrafficModelImportDisposition.Quarantined)
            {
                await WriteQuarantineReceiptAsync(current, cancellationToken).ConfigureAwait(false);
            }

            if (string.Equals(state.CurrentReceiptSha256, state.LastKnownGoodReceiptSha256, StringComparison.Ordinal))
            {
                return Result(current, state);
            }

            var fallback = await ImportStoredAsync(state.LastKnownGoodReceiptSha256, requiredScope, cancellationToken).ConfigureAwait(false);
            if (!fallback.IsAccepted)
            {
                if (fallback.Disposition == TrafficModelImportDisposition.Quarantined)
                {
                    await WriteQuarantineReceiptAsync(fallback, cancellationToken).ConfigureAwait(false);
                }

                return current.Disposition == TrafficModelImportDisposition.Incompatible
                    ? Result(current, state)
                    : Result(fallback, state);
            }

            if (current.Disposition == TrafficModelImportDisposition.Incompatible)
            {
                return Result(fallback, state) with { ReasonCode = "compatible-last-known-good" };
            }

            var rolledBack = state with
            {
                Revision = checked(state.Revision + 1),
                CurrentReceiptSha256 = state.LastKnownGoodReceiptSha256,
            };
            await WriteStateAsync(rolledBack, cancellationToken).ConfigureAwait(false);
            return Result(fallback, rolledBack) with { ReasonCode = "rolled-back-to-last-known-good" };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TrafficSnapshotInstallResult> RollbackAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await ReadStateAsync(cancellationToken).ConfigureAwait(false);
            if (state is null)
            {
                return new TrafficSnapshotInstallResult(
                    TrafficModelImportDisposition.Incompatible,
                    "no-installed-snapshot",
                    null,
                    null,
                    null);
            }

            var fallback = await ImportStoredAsync(state.LastKnownGoodReceiptSha256, requiredScope: null, cancellationToken)
                .ConfigureAwait(false);
            if (!fallback.IsAccepted)
            {
                await WriteQuarantineReceiptAsync(fallback, cancellationToken).ConfigureAwait(false);
                return Result(fallback, state);
            }

            var rolledBack = state with
            {
                Revision = checked(state.Revision + 1),
                CurrentReceiptSha256 = state.LastKnownGoodReceiptSha256,
            };
            await WriteStateAsync(rolledBack, cancellationToken).ConfigureAwait(false);
            return Result(fallback, rolledBack) with { ReasonCode = "explicit-rollback" };
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task<TrafficModelImportResult> ImportStoredAsync(
        string receiptSha256,
        TrafficCompatibilityScope? requiredScope,
        CancellationToken cancellationToken)
    {
        var directory = VersionDirectory(receiptSha256);
        try
        {
            if (!Directory.Exists(directory) ||
                new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException("Installed traffic snapshots must be ordinary local directories.");
            }

            await using var dataset = OpenMember(directory, "dataset.json");
            await using var report = OpenMember(directory, "build-report.json");
            await using var manifest = OpenMember(directory, "manifest.json");
            await using var signature = OpenMember(directory, "manifest.signature.json");
            await using var artifact = OpenMember(directory, "model.artifact");
            return await _importer.ImportAsync(
                new TrafficModelPackageStreams(dataset, report, manifest, signature, artifact),
                requiredScope,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new TrafficModelImportResult(
                TrafficModelImportDisposition.Quarantined,
                receiptSha256,
                "installed-package-unreadable",
                null,
                null,
                null,
                null,
                null,
                null);
        }
    }

    private async Task StageVersionAsync(
        TrafficModelImportResult imported,
        string target,
        CancellationToken cancellationToken)
    {
        var stagingRoot = Path.Combine(_rootDirectory, "staging");
        Directory.CreateDirectory(stagingRoot);
        var staging = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            await WriteFileAsync(Path.Combine(staging, "dataset.json"), imported.DatasetJson!, cancellationToken).ConfigureAwait(false);
            await WriteFileAsync(Path.Combine(staging, "build-report.json"), imported.BuildReportJson!, cancellationToken).ConfigureAwait(false);
            await WriteFileAsync(Path.Combine(staging, "manifest.json"), imported.ManifestJson!, cancellationToken).ConfigureAwait(false);
            await WriteFileAsync(Path.Combine(staging, "manifest.signature.json"), imported.SignatureJson!, cancellationToken).ConfigureAwait(false);
            await WriteFileAsync(Path.Combine(staging, "model.artifact"), imported.Artifact!, cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.Move(staging, target);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    private async Task<SnapshotState?> ReadStateAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_rootDirectory, "snapshot-state.json");
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > 64 * 1024)
        {
            throw new InvalidDataException("Traffic snapshot state is outside its byte bound.");
        }

        var state = await JsonSerializer.DeserializeAsync<SnapshotState>(stream, TrafficDataJson.Options, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("Traffic snapshot state was null.");
        ValidateState(state);
        return state;
    }

    private async Task WriteStateAsync(SnapshotState state, CancellationToken cancellationToken)
    {
        ValidateState(state);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, TrafficDataJson.Options);
        var path = Path.Combine(_rootDirectory, "snapshot-state.json");
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await WriteFileAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private async Task WriteQuarantineReceiptAsync(
        TrafficModelImportResult imported,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(_rootDirectory, "quarantine");
        Directory.CreateDirectory(directory);
        var receipt = new QuarantineReceipt(
            StateSchemaVersion,
            imported.ReceiptSha256,
            imported.Disposition,
            imported.ReasonCode,
            _timeProvider.GetUtcNow());
        var path = Path.Combine(directory, $"{_timeProvider.GetUtcNow():yyyyMMddHHmmssfffffff}-{Guid.NewGuid():N}.json");
        await WriteFileAsync(path, JsonSerializer.SerializeToUtf8Bytes(receipt, TrafficDataJson.Options), cancellationToken)
            .ConfigureAwait(false);

        foreach (var expired in Directory.GetFiles(directory, "*.json")
                     .OrderByDescending(candidate => candidate, StringComparer.Ordinal)
                     .Skip(_maximumQuarantineReceipts))
        {
            File.Delete(expired);
        }
    }

    private string VersionDirectory(string receiptSha256)
    {
        RequireSha256(receiptSha256);
        return Path.Combine(_rootDirectory, "versions", receiptSha256);
    }

    private void PruneVersions(SnapshotState state)
    {
        var root = Path.Combine(_rootDirectory, "versions");
        if (!Directory.Exists(root))
        {
            return;
        }

        var pinned = new HashSet<string>(StringComparer.Ordinal)
        {
            state.CurrentReceiptSha256,
            state.LastKnownGoodReceiptSha256,
        };
        var candidates = new DirectoryInfo(root).EnumerateDirectories()
            .Where(directory => !pinned.Contains(directory.Name) &&
                                IsSha256(directory.Name) &&
                                !directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            .OrderByDescending(directory => directory.LastWriteTimeUtc)
            .Skip(Math.Max(0, _maximumRetainedVersions - pinned.Count))
            .ToArray();
        foreach (var expired in candidates)
        {
            expired.Delete(recursive: true);
        }
    }

    private static FileStream OpenMember(string directory, string member) => new(
        Path.Combine(directory, member),
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 16 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static async Task WriteFileAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static TrafficSnapshotInstallResult Result(TrafficModelImportResult imported, SnapshotState? state) => new(
        imported.Disposition,
        imported.ReasonCode,
        state?.CurrentReceiptSha256,
        state?.LastKnownGoodReceiptSha256,
        imported.Publication);

    private static string? DowngradeReason(
        TrafficModelImportResult candidate,
        TrafficModelImportResult installed)
    {
        var candidateManifest = candidate.Publication!.Manifest;
        var installedManifest = installed.Publication!.Manifest;
        if (candidateManifest.DataThroughUtc < installedManifest.DataThroughUtc)
        {
            return "snapshot-data-through-regression";
        }

        if (candidateManifest.GeneratedUtc < installedManifest.GeneratedUtc)
        {
            return "snapshot-generation-regression";
        }

        return candidateManifest.GeneratedUtc == installedManifest.GeneratedUtc &&
               !HasSameSignedManifest(candidate, installed)
            ? "snapshot-generation-conflict"
            : null;
    }

    private static bool HasSameSignedManifest(
        TrafficModelImportResult left,
        TrafficModelImportResult right) =>
        left.ManifestJson!.AsSpan().SequenceEqual(right.ManifestJson!);

    private static TrafficModelImportResult RefusedInstall(
        TrafficModelImportResult imported,
        string reasonCode) =>
        new(
            TrafficModelImportDisposition.Quarantined,
            imported.ReceiptSha256,
            reasonCode,
            null,
            null,
            null,
            null,
            null,
            null);

    private static void ValidateState(SnapshotState state)
    {
        if (state.SchemaVersion != StateSchemaVersion || state.Revision < 1 || state.Revision == long.MaxValue)
        {
            throw new InvalidDataException("Traffic snapshot state has an unsupported schema or revision.");
        }

        RequireSha256(state.CurrentReceiptSha256);
        RequireSha256(state.LastKnownGoodReceiptSha256);
    }

    private static void RequireSha256(string value)
    {
        if (!IsSha256(value))
        {
            throw new InvalidDataException("Traffic snapshot state contains a non-canonical content address.");
        }
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private sealed record SnapshotState(
        int SchemaVersion,
        long Revision,
        string CurrentReceiptSha256,
        string LastKnownGoodReceiptSha256);

    private sealed record QuarantineReceipt(
        int SchemaVersion,
        string ReceiptSha256,
        TrafficModelImportDisposition Disposition,
        string ReasonCode,
        DateTimeOffset RecordedUtc);
}
