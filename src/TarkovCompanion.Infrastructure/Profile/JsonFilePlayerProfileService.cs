using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Profile;

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
    private readonly string _filePath;
    private readonly int _maximumImportBytes;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    public JsonFilePlayerProfileService(JsonProfileOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaximumImportBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Maximum import size must be positive.");
        }

        _filePath = options.ValidatedFilePath;
        _maximumImportBytes = options.MaximumImportBytes;
        _timeProvider = timeProvider ?? TimeProvider.System;
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

            if (new FileInfo(_filePath).Length > _maximumImportBytes)
            {
                throw new InvalidDataException($"Stored profile exceeds the {_maximumImportBytes}-byte limit.");
            }

            var json = await File.ReadAllTextAsync(_filePath, cancellationToken).ConfigureAwait(false);
            var export = ParseAndValidate(json);
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
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 16_384,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, export, SerializerOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
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
