using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Infrastructure.Persistence;

namespace TarkovCompanion.Infrastructure.Profile;

public sealed record JsonProfileOptions(string FilePath, int MaximumImportBytes = 1_048_576)
{
    public string ValidatedFilePath => string.IsNullOrWhiteSpace(FilePath)
        ? throw new ArgumentException("A profile file path is required.", nameof(FilePath))
        : Path.GetFullPath(FilePath);
}

public sealed class JsonFilePlayerProfileService : IPlayerProfileService, IDisposable
{
    public const int CurrentSchemaVersion = 2;

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly Encoding StrictUtf16LittleEndian = new UnicodeEncoding(
        bigEndian: false,
        byteOrderMark: false,
        throwOnInvalidBytes: true);
    private static readonly Encoding StrictUtf16BigEndian = new UnicodeEncoding(
        bigEndian: true,
        byteOrderMark: false,
        throwOnInvalidBytes: true);
    private static readonly Encoding StrictUtf32LittleEndian = new UTF32Encoding(
        bigEndian: false,
        byteOrderMark: false,
        throwOnInvalidCharacters: true);
    private static readonly Encoding StrictUtf32BigEndian = new UTF32Encoding(
        bigEndian: true,
        byteOrderMark: false,
        throwOnInvalidCharacters: true);
    private readonly string _filePath;
    private readonly int _maximumImportBytes;
    private readonly TimeProvider _timeProvider;
    private readonly SqliteConnectionFactory? _recoveryDatabase;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    public JsonFilePlayerProfileService(
        JsonProfileOptions options,
        TimeProvider? timeProvider = null,
        SqliteConnectionFactory? recoveryDatabase = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaximumImportBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Maximum import size must be positive.");
        }

        _filePath = options.ValidatedFilePath;
        _maximumImportBytes = options.MaximumImportBytes;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _recoveryDatabase = recoveryDatabase;
    }

    public async Task<PlayerProfile> GetActiveAsync(CancellationToken cancellationToken)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_filePath))
            {
                var profile = CreateDefaultProfile();
                await WriteProfileAsync(profile, cancellationToken).ConfigureAwait(false);
                return profile;
            }

            ProfileExport export;
            try
            {
                var stored = await ReadStoredProfileAsync(cancellationToken).ConfigureAwait(false);
                if (stored.OversizedBytes is { } storedBytes)
                {
                    return await RecoverOversizedProfileAsync(storedBytes, cancellationToken).ConfigureAwait(false);
                }

                export = ParseAndValidate(stored.Json!);
            }
            catch (InvalidDataException exception)
            {
                return await RecoverMalformedProfileAsync(exception, cancellationToken).ConfigureAwait(false);
            }

            // A valid legacy profile can serialize larger after migration. Keep a write-budget
            // failure distinct from malformed-input recovery so the valid original remains live.
            if (export.SchemaVersion < CurrentSchemaVersion)
            {
                await WriteProfileAsync(export.Profile, cancellationToken).ConfigureAwait(false);
            }

            return export.Profile;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task SaveAsync(PlayerProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var normalized = ValidateAndNormalize(profile);

        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteProfileAsync(normalized, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task<string> ExportJsonAsync(CancellationToken cancellationToken)
    {
        var profile = await GetActiveAsync(cancellationToken).ConfigureAwait(false);
        var export = new ProfileExport(CurrentSchemaVersion, profile, _timeProvider.GetUtcNow());
        return JsonSerializer.Serialize(export, SerializerOptions);
    }

    public async Task<PlayerProfile> ImportJsonAsync(string json, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > _maximumImportBytes)
        {
            throw new InvalidDataException($"Profile import exceeds the {_maximumImportBytes}-byte limit.");
        }

        var profile = ParseAndValidate(json).Profile;
        await SaveAsync(profile, cancellationToken).ConfigureAwait(false);
        return profile;
    }

    public void Dispose() => _fileLock.Dispose();

    private async Task WriteProfileAsync(PlayerProfile profile, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var export = new ProfileExport(CurrentSchemaVersion, profile, _timeProvider.GetUtcNow());
        var temporaryPath = _filePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var file = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 16_384,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await using var stream = new BoundedProfileWriteStream(file, _maximumImportBytes);
                await JsonSerializer.SerializeAsync(stream, export, SerializerOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            // The same limit guards both directions. Without checking our own serialization, a
            // valid large profile could be published successfully and then classified as hostile
            // on the next startup, where recovery would replace it with a default profile.
            var serializedBytes = new FileInfo(temporaryPath).Length;
            if (serializedBytes > _maximumImportBytes)
            {
                throw new InvalidDataException(
                    $"Serialized profile exceeds the {_maximumImportBytes}-byte limit.");
            }

            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    /// <summary>Reads at most one configured profile document, including while the file grows.</summary>
    /// <remarks>
    /// Checking <see cref="FileInfo.Length"/> before <c>ReadAllTextAsync</c> left a time-of-check
    /// gap and still trusted the text helper to allocate the entire file. The bounded byte loop
    /// observes at most one buffer beyond the limit and decodes only after the complete body is
    /// known to fit. Invalid Unicode is malformed profile evidence, not replacement characters.
    /// </remarks>
    private async Task<StoredProfileRead> ReadStoredProfileAsync(CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            _filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16_384,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > _maximumImportBytes)
        {
            return new(null, stream.Length);
        }

        using var output = new MemoryStream(capacity: Math.Min(_maximumImportBytes, 16_384));
        var buffer = new byte[16_384];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (output.Length > _maximumImportBytes - read)
            {
                return new(null, Math.Max(stream.Length, output.Length + read));
            }

            output.Write(buffer, 0, read);
        }

        try
        {
            return new(
                DecodeProfileText(output.GetBuffer().AsSpan(0, checked((int)output.Length))),
                null);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("Profile import is not valid Unicode text.", exception);
        }
    }

    private static string DecodeProfileText(ReadOnlySpan<byte> bytes)
    {
        // File.ReadAllText historically accepted the standard Unicode BOMs. Preserve that
        // compatibility while making each decoder reject malformed byte sequences explicitly.
        if (bytes.Length >= 4 &&
            bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
        {
            return StrictUtf32BigEndian.GetString(bytes[4..]);
        }

        if (bytes.Length >= 4 &&
            bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
        {
            return StrictUtf32LittleEndian.GetString(bytes[4..]);
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return StrictUtf8.GetString(bytes[3..]);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return StrictUtf16BigEndian.GetString(bytes[2..]);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return StrictUtf16LittleEndian.GetString(bytes[2..]);
        }

        return StrictUtf8.GetString(bytes);
    }

    /// <summary>
    /// Preserves an unreadable profile byte-for-byte, records explicit recovery state, and opens
    /// a fresh local profile so one malformed optional document cannot abort the whole app.
    /// </summary>
    /// <remarks>
    /// The backup is completed before the live file is replaced. If backup or recovery-state
    /// persistence fails, the exception escapes and the malformed original remains untouched.
    /// </remarks>
    private async Task<PlayerProfile> RecoverMalformedProfileAsync(
        InvalidDataException failure,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string hash;
        await using (var source = new FileStream(
                         _filePath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         bufferSize: 16_384,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            hash = Convert.ToHexStringLower(
                await SHA256.HashDataAsync(source, cancellationToken).ConfigureAwait(false));
        }
        var backupPath = RecoveryPath(
            // Recovery can be retried after the database write fails. A content hash and a
            // fixed/injected clock are not enough to make that second preservation attempt
            // unique, so keep every exact malformed artifact instead of failing on its name.
            $"{hash[..12]}.{Guid.NewGuid():N}.invalid");
        File.Copy(_filePath, backupPath, overwrite: false);

        await RecordRecoveryAsync(hash, "profile-json-invalid", cancellationToken).ConfigureAwait(false);

        var recovered = CreateDefaultProfile();
        await WriteProfileAsync(recovered, cancellationToken).ConfigureAwait(false);
        _ = failure; // The stable diagnostic code is persisted; raw parse details stay local.
        return recovered;
    }

    /// <summary>
    /// Quarantines an oversized profile without reading, hashing, or copying its untrusted length.
    /// </summary>
    /// <remarks>
    /// The recovery directory is beside the live file, so the move is a same-volume metadata
    /// operation. If recording recovery or writing the default fails, the exact original is moved
    /// back before the error escapes. This keeps the size limit from becoming a CPU/disk-amplifier
    /// while retaining the user's bytes for deliberate recovery.
    /// </remarks>
    private async Task<PlayerProfile> RecoverOversizedProfileAsync(
        long storedBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var backupPath = RecoveryPath($"{storedBytes}.oversize.{Guid.NewGuid():N}.invalid");
        File.Move(_filePath, backupPath);
        try
        {
            await RecordRecoveryAsync(null, "profile-json-oversize", cancellationToken).ConfigureAwait(false);
            var recovered = CreateDefaultProfile();
            await WriteProfileAsync(recovered, cancellationToken).ConfigureAwait(false);
            return recovered;
        }
        catch
        {
            // WriteProfileAsync replaces only after its temporary file is complete. If it did not
            // publish, restore the quarantined bytes to their original location in constant time.
            if (!File.Exists(_filePath) && File.Exists(backupPath))
            {
                File.Move(backupPath, _filePath);
            }

            throw;
        }
    }

    private async Task RecordRecoveryAsync(
        string? contentHash,
        string diagnosticCode,
        CancellationToken cancellationToken)
    {
        if (_recoveryDatabase is not null)
        {
            await using var connection = await _recoveryDatabase.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO local_json_recovery(
                    document_key, state, detected_utc, content_sha256, diagnostic_code)
                VALUES ('profile:active', 'malformed', $detected, $hash, $diagnostic)
                ON CONFLICT(document_key) DO UPDATE SET
                    state = excluded.state,
                    detected_utc = excluded.detected_utc,
                    content_sha256 = excluded.content_sha256,
                    diagnostic_code = excluded.diagnostic_code;
                """;
            command.Parameters.AddWithValue(
                "$detected",
                _timeProvider.GetUtcNow().ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$hash", (object?)contentHash ?? DBNull.Value);
            command.Parameters.AddWithValue("$diagnostic", diagnosticCode);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private string RecoveryPath(string qualifier)
    {
        var directory = Path.GetDirectoryName(_filePath) ?? Directory.GetCurrentDirectory();
        var recoveryDirectory = Path.Combine(directory, "Recovery");
        Directory.CreateDirectory(recoveryDirectory);
        var stem = Path.GetFileNameWithoutExtension(_filePath);
        var extension = Path.GetExtension(_filePath);
        var stamp = _timeProvider.GetUtcNow().ToUniversalTime()
            .ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        return Path.Combine(recoveryDirectory, $"{stem}.{stamp}.{qualifier}{extension}");
    }

    private ProfileExport ParseAndValidate(string json)
    {
        try
        {
            var export = JsonSerializer.Deserialize<ProfileExport>(json, SerializerOptions)
                ?? throw new InvalidDataException("Profile import is empty.");
            if (export.Profile is null)
            {
                throw new InvalidDataException("Profile import is missing the profile object.");
            }

            if (export.SchemaVersion is < 1 or > CurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    $"Unsupported profile schema {export.SchemaVersion}; supported versions are 1 through {CurrentSchemaVersion}.");
            }

            if (export.SchemaVersion == CurrentSchemaVersion && !HasProfileGeneration(json))
            {
                throw new InvalidDataException("Profile schema 2 requires an explicit profile generation.");
            }

            var profile = export.SchemaVersion == 1
                ? export.Profile with { ProfileGeneration = $"legacy-{export.Profile.Id:N}" }
                : export.Profile;
            return export with { Profile = ValidateAndNormalize(profile) };
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Profile import is not valid JSON for this schema.", exception);
        }
        catch (NotSupportedException exception)
        {
            throw new InvalidDataException("Profile import contains an unsupported value.", exception);
        }
    }

    private static PlayerProfile ValidateAndNormalize(PlayerProfile profile)
    {
        if (profile is null)
        {
            throw new InvalidDataException("Profile import is missing the profile object.");
        }

        if (profile.Id == Guid.Empty)
        {
            throw new InvalidDataException("Profile id is required.");
        }

        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            throw new InvalidDataException("Profile name is required.");
        }

        if (profile.Level is < 1 or > 100)
        {
            throw new InvalidDataException("Profile level must be between 1 and 100.");
        }

        if (string.IsNullOrWhiteSpace(profile.ProfileGeneration) || profile.ProfileGeneration.Trim().Length > 128)
        {
            throw new InvalidDataException("Profile generation is required and cannot exceed 128 characters.");
        }

        if (profile.TraderLevels is null ||
            profile.CompletedTaskIds is null ||
            profile.ObjectiveProgress is null ||
            profile.HideoutStationLevels is null ||
            profile.WishlistItemIds is null ||
            profile.OwnedItemCounts is null ||
            profile.EventItemStates is null ||
            profile.ItemOverrides is null)
        {
            throw new InvalidDataException("Profile collections are required.");
        }

        if (profile.TraderLevels.Any(x => x.Value is < 0 or > 4) ||
            profile.ObjectiveProgress.Any(x => x.Value < 0) ||
            profile.HideoutStationLevels.Any(x => x.Value < 0) ||
            profile.OwnedItemCounts.Any(x => x.Value < 0))
        {
            throw new InvalidDataException("Profile counts and levels cannot be negative or out of range.");
        }

        return profile with
        {
            Name = profile.Name.Trim(),
            TraderLevels = SortedDictionary(profile.TraderLevels),
            CompletedTaskIds = new HashSet<string>(profile.CompletedTaskIds, StringComparer.Ordinal),
            ObjectiveProgress = SortedDictionary(profile.ObjectiveProgress),
            HideoutStationLevels = SortedDictionary(profile.HideoutStationLevels),
            WishlistItemIds = new HashSet<string>(profile.WishlistItemIds, StringComparer.Ordinal),
            OwnedItemCounts = SortedDictionary(profile.OwnedItemCounts),
            EventItemStates = SortedDictionary(profile.EventItemStates),
            ItemOverrides = SortedDictionary(profile.ItemOverrides),
            UpdatedUtc = profile.UpdatedUtc.ToUniversalTime(),
            ProfileGeneration = profile.ProfileGeneration.Trim(),
        };
    }

    private static Dictionary<string, TValue> SortedDictionary<TValue>(
        IEnumerable<KeyValuePair<string, TValue>> values) => values
        .OrderBy(x => x.Key, StringComparer.Ordinal)
        .ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);

    private static bool HasProfileGeneration(string json)
    {
        using var document = JsonDocument.Parse(json, new()
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });
        var profile = document.RootElement.EnumerateObject()
            .FirstOrDefault(property => property.Name.Equals("profile", StringComparison.OrdinalIgnoreCase));
        if (profile.Value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var generation = profile.Value.EnumerateObject()
            .FirstOrDefault(property => property.Name.Equals("profileGeneration", StringComparison.OrdinalIgnoreCase));
        return generation.Value.ValueKind == JsonValueKind.String;
    }

    private PlayerProfile CreateDefaultProfile()
    {
        var now = _timeProvider.GetUtcNow();
        return new(
            Guid.NewGuid(),
            "Local profile",
            GameMode.Regular,
            1,
            Faction.Unknown,
            null,
            new Dictionary<string, int>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            new Dictionary<string, int>(StringComparer.Ordinal),
            new Dictionary<string, int>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            new Dictionary<string, int>(StringComparer.Ordinal),
            new Dictionary<string, EventItemState>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal),
            now,
            Guid.NewGuid().ToString("N"));
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            AllowTrailingCommas = true,
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            WriteIndented = true,
        };
        options.Converters.Add(new StringSetJsonConverter());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private readonly record struct StoredProfileRead(string? Json, long? OversizedBytes);

    /// <summary>Rejects the write that would first cross the durable profile byte budget.</summary>
    private sealed class BoundedProfileWriteStream(Stream inner, long maximumBytes) : Stream
    {
        private long _written;

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureFits(count);
            inner.Write(buffer, offset, count);
            _written += count;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureFits(buffer.Length);
            inner.Write(buffer);
            _written += buffer.Length;
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            EnsureFits(buffer.Length);
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            _written += buffer.Length;
        }

        public override async Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            EnsureFits(count);
            await inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
            _written += count;
        }

        private void EnsureFits(int count)
        {
            if (count < 0 || _written > maximumBytes - count)
            {
                throw new InvalidDataException(
                    $"Serialized profile exceeds the {maximumBytes}-byte limit.");
            }
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position
        {
            get => _written;
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class StringSetJsonConverter : JsonConverter<IReadOnlySet<string>>
    {
        public override IReadOnlySet<string> Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            var values = JsonSerializer.Deserialize<string[]>(ref reader, options)
                ?? throw new JsonException("Set value cannot be null.");
            return new HashSet<string>(values, StringComparer.Ordinal);
        }

        public override void Write(
            Utf8JsonWriter writer,
            IReadOnlySet<string> value,
            JsonSerializerOptions options) => JsonSerializer.Serialize(writer, value.Order(StringComparer.Ordinal), options);
    }
}
