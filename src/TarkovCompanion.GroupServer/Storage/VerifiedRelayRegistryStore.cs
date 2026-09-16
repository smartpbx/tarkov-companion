using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.GroupServer.Security;

namespace TarkovCompanion.GroupServer.Storage;

/// <summary>Checksummed atomic registry persistence with one independently verified backup.</summary>
/// <remarks>
/// Device storage is an authorization boundary, unlike the v1 room list. Missing, unreadable, or
/// corrupt state must never turn authentication off. A verified backup may be restored; otherwise
/// callers receive a closed state that accepts only the explicit owner-recovery ceremony.
/// </remarks>
public sealed class VerifiedRelayRegistryStore
{
    private const int SchemaVersion = 2;
    private readonly string _path;
    private readonly string _backupPath;
    private readonly JsonSerializerOptions _json;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _expectedGeneration;
    private bool _allowRecoveryOverwrite;
    private bool _loaded;

    public VerifiedRelayRegistryStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = System.IO.Path.GetFullPath(path);
        _backupPath = _path + ".backup";
        _json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            MaxDepth = ProtocolBounds.MaxJsonDepth,
            NumberHandling = JsonNumberHandling.Strict,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        _json.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
    }

    public string Path => _path;

    public string BackupPath => _backupPath;

    public async ValueTask<RelayRegistryLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path) && !File.Exists(_backupPath))
            {
                TrackLoad(generation: 0, allowRecoveryOverwrite: true);
                return new RelayRegistryLoadResult(RelayRegistryState.Empty, RelayRegistryLoadStatus.Uninitialized);
            }

            var primary = await TryReadVerifiedAsync(_path, cancellationToken).ConfigureAwait(false);
            var backup = await TryReadVerifiedAsync(_backupPath, cancellationToken).ConfigureAwait(false);
            if (primary is not null && backup is not null && primary.Generation == backup.Generation &&
                !string.Equals(primary.PayloadDigestBase64Url, backup.PayloadDigestBase64Url, StringComparison.Ordinal))
            {
                // Equal generations are written from one immutable state. Divergence means at
                // least one independently checksummed copy was replaced, so choosing either one
                // would turn ambiguity at an authorization boundary into authority.
                TrackLoad(generation: 0, allowRecoveryOverwrite: true);
                return new RelayRegistryLoadResult(RelayRegistryState.Empty, RelayRegistryLoadStatus.Corrupt);
            }

            if (primary is not null && (backup is null || primary.Generation >= backup.Generation))
            {
                if (backup is null || primary.Generation > backup.Generation)
                {
                    // Authentication must not continue beside a stale fallback. Otherwise a later
                    // primary failure could resurrect a credential or device revoked after the
                    // backup's generation.
                    await WriteEnvelopeAtomicAsync(
                        _backupPath,
                        primary.State,
                        primary.Generation,
                        cancellationToken).ConfigureAwait(false);
                }

                TrackLoad(primary.Generation, allowRecoveryOverwrite: false);
                return new RelayRegistryLoadResult(primary.State, RelayRegistryLoadStatus.PrimaryVerified);
            }

            if (backup is null)
            {
                TrackLoad(generation: 0, allowRecoveryOverwrite: true);
                return new RelayRegistryLoadResult(RelayRegistryState.Empty, RelayRegistryLoadStatus.Corrupt);
            }

            await WriteEnvelopeAtomicAsync(_path, backup.State, backup.Generation, cancellationToken).ConfigureAwait(false);
            TrackLoad(backup.Generation, allowRecoveryOverwrite: false);
            return new RelayRegistryLoadResult(backup.State, RelayRegistryLoadStatus.BackupRestored);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask SaveAsync(RelayRegistryState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ValidateOperationalState(state);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_loaded)
            {
                throw new InvalidOperationException("The relay registry must be loaded before it can be saved.");
            }

            var primary = await TryReadVerifiedAsync(_path, cancellationToken).ConfigureAwait(false);
            var backup = await TryReadVerifiedAsync(_backupPath, cancellationToken).ConfigureAwait(false);
            if (primary is not null && backup is not null && primary.Generation == backup.Generation &&
                !string.Equals(primary.PayloadDigestBase64Url, backup.PayloadDigestBase64Url, StringComparison.Ordinal) &&
                !_allowRecoveryOverwrite)
            {
                throw new InvalidDataException("Equal registry generations diverged.");
            }

            var currentGeneration = Math.Max(primary?.Generation ?? 0, backup?.Generation ?? 0);
            if (!_allowRecoveryOverwrite && currentGeneration != _expectedGeneration)
            {
                throw new InvalidDataException("The relay registry generation changed or disappeared after verification.");
            }

            var generation = _allowRecoveryOverwrite && currentGeneration >= ProtocolBounds.MaxWireInteger
                ? 1
                : currentGeneration < ProtocolBounds.MaxWireInteger
                    ? currentGeneration + 1
                    : throw new InvalidDataException("The relay registry generation is exhausted.");
            // Backup first: if the process stops before replacing primary, LoadAsync sees both
            // verified generations and promotes the newer backup. After success both copies are
            // the same generation, so restoring one can never resurrect a revoked credential.
            await WriteEnvelopeAtomicAsync(_backupPath, state, generation, cancellationToken).ConfigureAwait(false);
            await WriteEnvelopeAtomicAsync(_path, state, generation, cancellationToken).ConfigureAwait(false);
            TrackLoad(generation, allowRecoveryOverwrite: false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void TrackLoad(long generation, bool allowRecoveryOverwrite)
    {
        _expectedGeneration = generation;
        _allowRecoveryOverwrite = allowRecoveryOverwrite;
        _loaded = true;
    }

    private async ValueTask<VerifiedRegistry?> TryReadVerifiedAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is <= 0 or > RelaySecurityBounds.MaximumRegistryBytes)
            {
                return null;
            }

            var bytes = await ReadBoundedAsync(path, cancellationToken).ConfigureAwait(false);
            if (bytes is null)
            {
                return null;
            }

            var envelope = JsonSerializer.Deserialize<StoredRegistryEnvelope>(bytes, _json);
            if (envelope is null || envelope.SchemaVersion != SchemaVersion ||
                envelope.Generation is <= 0 or > ProtocolBounds.MaxWireInteger ||
                envelope.Payload is null ||
                string.IsNullOrWhiteSpace(envelope.PayloadSha256Base64Url))
            {
                return null;
            }

            var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(envelope.Payload, _json);
            var actualDigest = SHA256.HashData(payloadBytes);
            var expectedDigest = RelayCsrfProtector.DecodeBase64Url(envelope.PayloadSha256Base64Url);
            if (expectedDigest.Length != SHA256.HashSizeInBytes ||
                !CryptographicOperations.FixedTimeEquals(expectedDigest, actualDigest))
            {
                return null;
            }

            ValidateOperationalState(envelope.Payload);
            return new VerifiedRegistry(envelope.Payload, envelope.Generation, envelope.PayloadSha256Base64Url);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            JsonException or ArgumentException or NotSupportedException or FormatException)
        {
            return null;
        }
    }

    private static async ValueTask<byte[]?> ReadBoundedAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var capacity = RelaySecurityBounds.MaximumRegistryBytes + 1;
        var rented = ArrayPool<byte>.Shared.Rent(capacity);
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var total = 0;
            while (total < capacity)
            {
                var read = await stream.ReadAsync(
                    rented.AsMemory(total, capacity - total),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            return total is > 0 and <= RelaySecurityBounds.MaximumRegistryBytes
                ? rented.AsSpan(0, total).ToArray()
                : null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    private async ValueTask WriteEnvelopeAtomicAsync(
        string path,
        RelayRegistryState state,
        long generation,
        CancellationToken cancellationToken)
    {
        if (generation is <= 0 or > ProtocolBounds.MaxWireInteger)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }

        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(state, _json);
        var envelope = new StoredRegistryEnvelope(
            SchemaVersion,
            generation,
            RelayCsrfProtector.Base64Url(SHA256.HashData(payloadBytes)),
            state);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, _json);
        if (bytes.Length > RelaySecurityBounds.MaximumRegistryBytes)
        {
            throw new InvalidDataException("The relay registry exceeds its storage bound.");
        }

        var directory = System.IO.Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("A registry directory is required.");
        Directory.CreateDirectory(directory);
        var temporary = path + ".writing-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static void ValidateOperationalState(RelayRegistryState state)
    {
        _ = new RelayRegistryState(state.IsInitialized, state.Devices, state.Sessions, state.Audit);
    }

    private sealed record StoredRegistryEnvelope(
        int SchemaVersion,
        long Generation,
        string PayloadSha256Base64Url,
        RelayRegistryState Payload);

    private sealed record VerifiedRegistry(
        RelayRegistryState State,
        long Generation,
        string PayloadDigestBase64Url);
}
