using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TarkovCompanion.Application.Services.LootSpawns;

namespace TarkovCompanion.Infrastructure.GameData.LootSpawns;

/// <summary>A bounded, atomic, restart-durable last-known-good loot publication head.</summary>
/// <remarks>
/// The payload is domain JSON inside a small versioned frame containing its exact byte length and
/// SHA-256. Replacement takes an inter-process lease, validates against the head read from disk,
/// writes through a same-directory temporary file, and atomically keeps the prior generation as a
/// fallback. A corrupt head is retained in one bounded set-aside path and recovered from that
/// fallback; neither corruption nor a failed write can silently clear the last-known-good bundle.
/// </remarks>
public sealed class DurableLootSpawnPublicationStore :
    ILootSpawnSourcePublicationStore,
    IReviewedLootSpawnPublicationReplacementStore,
    IDisposable
{
    public const int FormatVersion = 1;
    public const long MaximumPayloadBytes = 256L * 1024 * 1024;

    public const long MaximumReplacementAuditBytes = 256L * 1024;

    private const int MaximumHeaderBytes = 256;
    private const int MaximumLeaseAttempts = 400;
    private const string Magic = "TARKOV-COMPANION-LOOT-PUBLICATION";
    private static readonly TimeSpan LeaseRetryDelay = TimeSpan.FromMilliseconds(25);
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly string _path;
    private readonly string _backupPath;
    private readonly string _leasePath;
    private readonly string _corruptPath;
    private readonly string _corruptBackupPath;
    private readonly string _replacementAuditPath;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<LootSpawnQuarantineEntry> _quarantine = [];
    private bool _disposed;

    public DurableLootSpawnPublicationStore(string path, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _backupPath = _path + ".previous";
        _leasePath = _path + ".lock";
        _corruptPath = _path + ".corrupt";
        _corruptBackupPath = _backupPath + ".corrupt";
        _replacementAuditPath = _path + ".replacement-audit.json";
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<LootSpawnSourceBundle?> ReadLastKnownGoodAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var lease = await AcquireLeaseAsync(cancellationToken).ConfigureAwait(false);
            return await ReadAndRecoverAsync(cancellationToken).ConfigureAwait(false);
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
            await using var lease = await AcquireLeaseAsync(cancellationToken).ConfigureAwait(false);
            var current = await ReadAndRecoverAsync(cancellationToken).ConfigureAwait(false);
            if (current is not null)
            {
                AtomicLootSpawnPublicationStore.ValidateReplacement(bundle, current, cancellationToken);
            }

            await WritePublicationAsync(bundle, cancellationToken).ConfigureAwait(false);
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
            await using var lease = await AcquireLeaseAsync(cancellationToken).ConfigureAwait(false);
            var current = await ReadAndRecoverAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new LootSpawnSourceImportException(
                    "publication.replacement-head-missing",
                    "An authorized replacement requires an existing publication head.");
            AtomicLootSpawnPublicationStore.ValidateAuthorizedReplacement(
                bundle,
                current,
                authorization,
                cancellationToken);

            // Authorization is recorded before the publication changes. If the later durable
            // write fails, the journal still truthfully says which exact candidate was approved;
            // it does not claim that candidate became the head.
            var audit = await ReadReplacementAuditCoreAsync(cancellationToken).ConfigureAwait(false);
            var nextAudit = audit
                .Append(AtomicLootSpawnPublicationStore.AuditEntry(
                    bundle,
                    current,
                    authorization,
                    UtcNow()))
                .TakeLast(AtomicLootSpawnPublicationStore.MaximumReplacementAuditEntries)
                .ToArray();
            await WriteReplacementAuditAsync(nextAudit, cancellationToken).ConfigureAwait(false);
            await WritePublicationAsync(bundle, cancellationToken).ConfigureAwait(false);
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
            await using var lease = await AcquireLeaseAsync(cancellationToken).ConfigureAwait(false);
            return await ReadReplacementAuditCoreAsync(cancellationToken).ConfigureAwait(false);
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
            AddQuarantine(diagnostic, detectedUtc);
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

    private async ValueTask<LootSpawnSourceBundle?> ReadAndRecoverAsync(CancellationToken cancellationToken)
    {
        var primary = await TryReadPublicationAsync(_path, cancellationToken).ConfigureAwait(false);
        if (primary.Bundle is not null)
        {
            return primary.Bundle;
        }

        var backup = await TryReadPublicationAsync(_backupPath, cancellationToken).ConfigureAwait(false);
        if (backup.Bundle is not null)
        {
            var canRestore = primary.State == PublicationFileState.Missing ||
                             TrySetAside(_path, _corruptPath);
            if (canRestore)
            {
                try
                {
                    await CopyFileAtomicallyAsync(_backupPath, _path, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // The valid fallback remains readable even when repair cannot be completed.
                }
            }

            AddQuarantine(
                new(
                    "publication.cache-recovered",
                    "The durable loot publication head was unavailable or corrupt; its validated previous generation was retained."),
                UtcNow());
            return backup.Bundle;
        }

        var corrupt = primary.State == PublicationFileState.Corrupt ||
                      backup.State == PublicationFileState.Corrupt;
        if (primary.State == PublicationFileState.Corrupt)
        {
            TrySetAside(_path, _corruptPath);
        }

        if (backup.State == PublicationFileState.Corrupt)
        {
            TrySetAside(_backupPath, _corruptBackupPath);
        }

        if (corrupt)
        {
            AddQuarantine(
                new(
                    "publication.cache-corrupt",
                    "No validated durable loot publication generation was available; corrupt bounded cache files were retained for diagnosis."),
                UtcNow());
        }

        return null;
    }

    private async ValueTask<PublicationReadResult> TryReadPublicationAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var initialLength = stream.Length;
            if (initialLength <= 0 || initialLength > MaximumPayloadBytes + MaximumHeaderBytes)
            {
                throw new InvalidDataException("The publication file is empty or exceeds its byte budget.");
            }

            var prefixLength = checked((int)Math.Min(initialLength, MaximumHeaderBytes));
            var prefix = new byte[prefixLength];
            await stream.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
            var headerLength = FindHeaderLength(prefix);
            var header = ParseHeader(prefix.AsSpan(0, headerLength));
            if (header.PayloadLength <= 0 || header.PayloadLength > MaximumPayloadBytes ||
                headerLength + header.PayloadLength != initialLength)
            {
                throw new InvalidDataException("The publication frame length is invalid.");
            }

            stream.Position = headerLength;
            using (var hashed = new LengthLimitedReadStream(stream, header.PayloadLength))
            {
                var actualHash = Convert.ToHexStringLower(
                    await SHA256.HashDataAsync(hashed, cancellationToken).ConfigureAwait(false));
                if (hashed.Remaining != 0 ||
                    !string.Equals(actualHash, header.PayloadSha256, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("The publication payload failed its content hash check.");
                }
            }

            stream.Position = headerLength;
            using var payload = new LengthLimitedReadStream(stream, header.PayloadLength);
            var bundle = await JsonSerializer
                .DeserializeAsync<LootSpawnSourceBundle>(payload, JsonOptions, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException("The publication payload is empty.");
            if (payload.Remaining != 0 || stream.Length != initialLength)
            {
                throw new InvalidDataException("The publication payload was not consumed exactly.");
            }

            return new(PublicationFileState.Valid, bundle);
        }
        catch (FileNotFoundException)
        {
            return new(PublicationFileState.Missing, null);
        }
        catch (DirectoryNotFoundException)
        {
            return new(PublicationFileState.Missing, null);
        }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException or
                                              JsonException or ArgumentException or InvalidOperationException or
                                              NotSupportedException or OverflowException or KeyNotFoundException)
        {
            return new(PublicationFileState.Corrupt, null);
        }
    }

    private async ValueTask WritePublicationAsync(
        LootSpawnSourceBundle bundle,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("The publication path has no parent directory.");
        Directory.CreateDirectory(directory);
        var payloadPath = _path + ".payload-writing";
        var framedPath = _path + ".writing";
        var backupWritingPath = _path + ".backup-writing";
        try
        {
            // The lease makes fixed scratch paths safe and keeps abrupt-process leftovers bounded.
            TryDelete(payloadPath);
            TryDelete(framedPath);
            TryDelete(backupWritingPath);
            long payloadLength;
            await using (var file = new FileStream(
                             payloadPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await using var bounded = new MaximumLengthWriteStream(file, MaximumPayloadBytes);
                await JsonSerializer.SerializeAsync(bounded, bundle, JsonOptions, cancellationToken).ConfigureAwait(false);
                await bounded.FlushAsync(cancellationToken).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
                payloadLength = bounded.LengthWritten;
            }

            if (payloadLength <= 0)
            {
                throw new InvalidDataException("The publication serializer produced an empty payload.");
            }

            var payloadSha256 = await HashFileAsync(payloadPath, cancellationToken).ConfigureAwait(false);
            var header = Encoding.ASCII.GetBytes(string.Join(
                '\n',
                Magic,
                FormatVersion.ToString(CultureInfo.InvariantCulture),
                payloadLength.ToString(CultureInfo.InvariantCulture),
                payloadSha256) + "\n");
            if (header.Length > MaximumHeaderBytes)
            {
                throw new InvalidDataException("The publication frame header exceeds its byte budget.");
            }

            await using (var destination = new FileStream(
                             framedPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await destination.WriteAsync(header, cancellationToken).ConfigureAwait(false);
                await using var payload = new FileStream(
                    payloadPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await payload.CopyToAsync(destination, 81920, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                destination.Flush(flushToDisk: true);
            }

            if (File.Exists(_path))
            {
                TryDelete(_backupPath);
                File.Replace(framedPath, _path, _backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                await CopyFileAsync(framedPath, backupWritingPath, cancellationToken).ConfigureAwait(false);
                File.Move(framedPath, _path, overwrite: false);
                try
                {
                    File.Move(backupWritingPath, _backupPath, overwrite: true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // The primary commit succeeded. Redundancy can be re-established by the next
                    // successful replacement without reporting this publication as failed.
                }
            }
        }
        finally
        {
            TryDelete(payloadPath);
            TryDelete(framedPath);
            TryDelete(backupWritingPath);
        }
    }

    private async ValueTask<IReadOnlyList<LootSpawnPublicationReplacementAuditEntry>>
        ReadReplacementAuditCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                _replacementAuditPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var initialLength = stream.Length;
            if (initialLength <= 0 || initialLength > MaximumReplacementAuditBytes)
            {
                throw new InvalidDataException("The replacement authorization journal exceeds its byte budget.");
            }

            await using var bounded = new LengthLimitedReadStream(stream, initialLength);
            var entries = await JsonSerializer
                .DeserializeAsync<LootSpawnPublicationReplacementAuditEntry[]>(
                    bounded,
                    JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException("The replacement authorization journal is empty.");
            if (bounded.Remaining != 0 || stream.Length != initialLength ||
                entries.Length > AtomicLootSpawnPublicationStore.MaximumReplacementAuditEntries ||
                entries.Any(entry => entry is null))
            {
                throw new InvalidDataException("The replacement authorization journal is invalid or oversized.");
            }

            return Array.AsReadOnly(entries);
        }
        catch (FileNotFoundException)
        {
            return [];
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or
                                              ArgumentException or InvalidOperationException or
                                              NotSupportedException or OverflowException or KeyNotFoundException)
        {
            throw new LootSpawnSourceImportException(
                "publication.replacement-audit-corrupt",
                "The bounded replacement authorization journal is corrupt; it was retained and no reviewed replacement was applied.",
                exception);
        }
    }

    private async ValueTask WriteReplacementAuditAsync(
        IReadOnlyList<LootSpawnPublicationReplacementAuditEntry> entries,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_replacementAuditPath)
            ?? throw new InvalidOperationException("The replacement authorization journal has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = _replacementAuditPath + ".writing";
        try
        {
            TryDelete(temporaryPath);
            await using (var file = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await using var bounded = new MaximumLengthWriteStream(file, MaximumReplacementAuditBytes);
                await JsonSerializer
                    .SerializeAsync(bounded, entries, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await bounded.FlushAsync(cancellationToken).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _replacementAuditPath, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private async ValueTask<FileStream> AcquireLeaseAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_leasePath)
            ?? throw new InvalidOperationException("The publication lease path has no parent directory.");
        Directory.CreateDirectory(directory);
        IOException? lastException = null;
        for (var attempt = 0; attempt < MaximumLeaseAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    _leasePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.Asynchronous);
            }
            catch (IOException exception)
            {
                lastException = exception;
                await Task.Delay(LeaseRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new LootSpawnSourceImportException(
            "publication.store-busy",
            "The durable loot publication store remained leased by another writer.",
            lastException);
    }

    private async ValueTask CopyFileAtomicallyAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        _ = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("The publication recovery path has no parent directory.");
        var temporary = destination + ".recovery-writing";
        try
        {
            TryDelete(temporary);
            await CopyFileAsync(source, temporary, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static async ValueTask CopyFileAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await input.CopyToAsync(output, 81920, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
    }

    private static async ValueTask<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static int FindHeaderLength(ReadOnlySpan<byte> prefix)
    {
        var lineFeeds = 0;
        for (var index = 0; index < prefix.Length; index++)
        {
            if (prefix[index] > 0x7f)
            {
                throw new InvalidDataException("The publication frame header is not ASCII.");
            }

            if (prefix[index] == (byte)'\n' && ++lineFeeds == 4)
            {
                return index + 1;
            }
        }

        throw new InvalidDataException("The publication frame header is incomplete.");
    }

    private static PublicationHeader ParseHeader(ReadOnlySpan<byte> bytes)
    {
        var text = Encoding.ASCII.GetString(bytes);
        var lines = text.Split('\n', StringSplitOptions.None);
        if (lines.Length != 5 || lines[^1].Length != 0 ||
            !string.Equals(lines[0], Magic, StringComparison.Ordinal) ||
            !int.TryParse(lines[1], NumberStyles.None, CultureInfo.InvariantCulture, out var version) ||
            version != FormatVersion ||
            !long.TryParse(lines[2], NumberStyles.None, CultureInfo.InvariantCulture, out var payloadLength) ||
            lines[3].Length != 64 || lines[3].Any(value =>
                !((value >= '0' && value <= '9') || (value >= 'a' && value <= 'f'))))
        {
            throw new InvalidDataException("The publication frame header is invalid or unsupported.");
        }

        return new(payloadLength, lines[3]);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            MaxDepth = 64,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            WriteIndented = false,
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private void AddQuarantine(LootSpawnSourceDiagnostic diagnostic, DateTimeOffset detectedUtc)
    {
        _quarantine.Add(new(detectedUtc, diagnostic));
        if (_quarantine.Count > AtomicLootSpawnPublicationStore.MaximumQuarantineEntries)
        {
            _quarantine.RemoveRange(
                0,
                _quarantine.Count - AtomicLootSpawnPublicationStore.MaximumQuarantineEntries);
        }
    }

    private DateTimeOffset UtcNow()
    {
        var value = _timeProvider.GetUtcNow();
        return value.Offset == TimeSpan.Zero ? value : value.ToUniversalTime();
    }

    private static bool TrySetAside(string source, string destination)
    {
        try
        {
            if (!File.Exists(source))
            {
                return true;
            }

            File.Move(source, destination, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private enum PublicationFileState
    {
        Missing,
        Valid,
        Corrupt,
    }

    private sealed record PublicationReadResult(PublicationFileState State, LootSpawnSourceBundle? Bundle);

    private sealed record PublicationHeader(long PayloadLength, string PayloadSha256);

    private sealed class LengthLimitedReadStream(Stream inner, long length) : Stream
    {
        public long Remaining { get; private set; } = length;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => length - Remaining;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer[..(int)Math.Min(buffer.Length, Remaining)]);
            Remaining -= read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await inner
                .ReadAsync(buffer[..(int)Math.Min(buffer.Length, Remaining)], cancellationToken)
                .ConfigureAwait(false);
            Remaining -= read;
            return read;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class MaximumLengthWriteStream(Stream inner, long maximumLength) : Stream
    {
        public long LengthWritten { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => LengthWritten;

        public override long Position
        {
            get => LengthWritten;
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Reserve(buffer.Length);
            inner.Write(buffer);
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            Reserve(buffer.Length);
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        private void Reserve(int count)
        {
            if (count < 0 || LengthWritten > maximumLength - count)
            {
                throw new InvalidDataException("The publication payload exceeds its byte budget.");
            }

            LengthWritten += count;
        }
    }
}
