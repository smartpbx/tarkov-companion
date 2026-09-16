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
    private const int SchemaVersion = 1;
    private readonly string _path;
    private readonly string _backupPath;
    private readonly JsonSerializerOptions _json;
    private readonly SemaphoreSlim _gate = new(1, 1);

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
                return new RelayRegistryLoadResult(RelayRegistryState.Empty, RelayRegistryLoadStatus.Uninitialized);
            }

            var primary = await TryReadVerifiedAsync(_path, cancellationToken).ConfigureAwait(false);
            var backup = await TryReadVerifiedAsync(_backupPath, cancellationToken).ConfigureAwait(false);
            if (primary is not null && (backup is null || primary.Generation >= backup.Generation))
            {
                return new RelayRegistryLoadResult(primary.State, RelayRegistryLoadStatus.PrimaryVerified);
            }

            if (backup is null)
            {
                return new RelayRegistryLoadResult(RelayRegistryState.Empty, RelayRegistryLoadStatus.Corrupt);
            }

            await WriteEnvelopeAtomicAsync(_path, backup.State, backup.Generation, cancellationToken).ConfigureAwait(false);
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
            var primary = await TryReadVerifiedAsync(_path, cancellationToken).ConfigureAwait(false);
            var backup = await TryReadVerifiedAsync(_backupPath, cancellationToken).ConfigureAwait(false);
            var generation = checked(Math.Max(primary?.Generation ?? 0, backup?.Generation ?? 0) + 1);
            // Backup first: if the process stops before replacing primary, LoadAsync sees both
            // verified generations and promotes the newer backup. After success both copies are
            // the same generation, so restoring one can never resurrect a revoked credential.
            await WriteEnvelopeAtomicAsync(_backupPath, state, generation, cancellationToken).ConfigureAwait(false);
            await WriteEnvelopeAtomicAsync(_path, state, generation, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
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

            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (bytes.Length is <= 0 or > RelaySecurityBounds.MaximumRegistryBytes)
            {
                return null;
            }

            var envelope = JsonSerializer.Deserialize<StoredRegistryEnvelope>(bytes, _json);
            if (envelope is null || envelope.SchemaVersion != SchemaVersion || envelope.Generation <= 0 ||
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
            return new VerifiedRegistry(envelope.Payload, envelope.Generation);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            JsonException or ArgumentException or NotSupportedException or FormatException)
        {
            return null;
        }
    }

    private async ValueTask WriteEnvelopeAtomicAsync(
        string path,
        RelayRegistryState state,
        long generation,
        CancellationToken cancellationToken)
    {
        if (generation <= 0)
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

    private sealed record VerifiedRegistry(RelayRegistryState State, long Generation);
}
