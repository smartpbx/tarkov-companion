using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.Infrastructure.Profile;

public sealed record ProjectQuestProgressJsonOptions(
    string AppVersion,
    int MaximumFileBytes = 2_097_152,
    int MaximumDepth = 32,
    int MaximumProfiles = 8,
    int MaximumRecords = 50_000,
    int MaximumStringLength = 4_096);

public sealed class ProjectQuestProgressJson(
    ProjectQuestProgressJsonOptions options,
    TimeProvider? timeProvider = null) : IProjectQuestProgressJson
{
    private const string ProvenanceSummary = "Local user-owned quest progress; raw source identities omitted.";
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<ProjectQuestProgressDocument> ReadAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        var path = RequiredPath(filePath);
        var bytes = await ReadBoundedAsync(path, cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = Positive(options.MaximumDepth, nameof(options.MaximumDepth)),
            });
            ValidateAllStrings(document.RootElement);
            return document.RootElement.TryGetProperty("formatId", out _)
                ? ParseVersionTwo(document.RootElement)
                : ParseLegacyProfileSettings(document.RootElement);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Quest progress import is not valid bounded JSON.", exception);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("Quest progress import contains a number outside the supported range.", exception);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("Quest progress import contains an invalid value.", exception);
        }
    }

    public async Task<QuestProgressExportResult> WriteAsync(
        string filePath,
        PlayerProfile profile,
        QuestProgressSnapshot progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(progress);
        if (profile.Id != progress.Scope.ProfileId ||
            profile.GameMode != progress.Scope.GameMode ||
            !string.Equals(profile.ProfileGeneration, progress.Scope.Generation, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The export profile does not match the exact progress scope.");
        }

        EnsureSafeRequired(profile.Name, "profile name");
        EnsureSafeRequired(profile.ProfileGeneration, "profile generation");
        EnsureSafeRequired(options.AppVersion, "application version");
        var exportProfile = new ProjectQuestProgressProfile(
            profile.Id,
            profile.Name,
            profile.GameMode,
            profile.ProfileGeneration,
            progress.Tasks.Values
                .OrderBy(value => value.TaskId, StringComparer.Ordinal)
                .Select(value => new ProjectQuestProgressTask(value.TaskId, value.State))
                .ToArray(),
            progress.Objectives.Values
                .OrderBy(value => value.ObjectiveId, StringComparer.Ordinal)
                .Select(value => new ProjectQuestProgressObjective(value.ObjectiveId, value.State, value.Count))
                .ToArray(),
            progress.ItemHoldings
                .OrderBy(value => value.ItemId, StringComparer.Ordinal)
                .ThenBy(value => value.FoundInRaid)
                .Select(value => new ProjectQuestProgressHolding(value.ItemId, value.FoundInRaid, value.Count))
                .ToArray(),
            progress.Pins
                .OrderBy(value => value.TargetKind)
                .ThenBy(value => value.TargetId, StringComparer.Ordinal)
                .Select(value => new ProjectQuestProgressPin(
                    value.TargetKind,
                    value.TargetId,
                    value.SortOrder,
                    SafeOptionalText(value.Note)))
                .ToArray());
        ValidateProfileForWrite(exportProfile);
        var profiles = new[] { exportProfile };
        var payloadBytes = CanonicalPayload(profiles);
        if (payloadBytes.Length > Positive(options.MaximumFileBytes, nameof(options.MaximumFileBytes)))
        {
            throw new InvalidOperationException("The normalized quest progress payload exceeds the export file limit.");
        }

        var payloadHash = Sha256(payloadBytes);
        var path = RequiredPath(filePath);
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The export path must have a parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16_384,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
                writer.WriteStartObject();
                writer.WriteString("formatId", ProjectQuestProgressFormat.Identifier);
                writer.WriteNumber("formatVersion", ProjectQuestProgressFormat.Version);
                writer.WriteString("appVersion", RequiredString(options.AppVersion, "app version", 128));
                writer.WriteString("exportedUtc", _timeProvider.GetUtcNow().ToUniversalTime());
                writer.WriteStartObject("provenance");
                writer.WriteString("summary", ProvenanceSummary);
                writer.WriteEndObject();
                writer.WritePropertyName("payload");
                writer.WriteRawValue(payloadBytes, skipInputValidation: false);
                writer.WriteStartObject("checksum");
                writer.WriteString("algorithm", "sha256");
                writer.WriteString("value", payloadHash);
                writer.WriteEndObject();
                writer.WriteEndObject();
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (new FileInfo(temporaryPath).Length > Positive(options.MaximumFileBytes, nameof(options.MaximumFileBytes)))
            {
                throw new InvalidOperationException("The quest progress export exceeds the configured file limit.");
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }

        return new(payloadHash, CountRecords(exportProfile));
    }

    private ProjectQuestProgressDocument ParseVersionTwo(JsonElement root)
    {
        RequireObject(root, "Quest progress envelope");
        var formatId = RequiredString(root, "formatId", 128);
        if (!formatId.Equals(ProjectQuestProgressFormat.Identifier, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unsupported quest progress format '{formatId}'.");
        }

        var formatVersion = RequiredInt32(root, "formatVersion");
        if (formatVersion != ProjectQuestProgressFormat.Version)
        {
            throw new InvalidDataException(
                $"Unsupported quest progress format version {formatVersion}; only version 2 is supported.");
        }

        var appVersion = RequiredString(root, "appVersion", 128);
        if (LooksSensitive(appVersion))
        {
            throw new InvalidDataException("Quest progress application version contains disallowed sensitive-looking data.");
        }

        var exportedUtc = RequiredUtc(root, "exportedUtc");
        var provenance = RequiredProperty(root, "provenance");
        RequireObject(provenance, "provenance");
        var provenanceSummary = RequiredString(provenance, "summary", options.MaximumStringLength);
        if (LooksSensitive(provenanceSummary))
        {
            throw new InvalidDataException("Quest progress provenance summary contains disallowed sensitive-looking data.");
        }
        var payload = RequiredProperty(root, "payload");
        RequireObject(payload, "payload");
        var profileElements = RequiredProperty(payload, "profiles");
        if (profileElements.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Quest progress payload profiles must be an array.");
        }

        if (profileElements.GetArrayLength() is < 1 || profileElements.GetArrayLength() > 8 ||
            profileElements.GetArrayLength() > Positive(options.MaximumProfiles, nameof(options.MaximumProfiles)))
        {
            throw new InvalidDataException(
                $"Quest progress imports require 1 through {options.MaximumProfiles} profiles.");
        }

        var profiles = new List<ProjectQuestProgressProfile>();
        var profileKeys = new HashSet<string>(StringComparer.Ordinal);
        var recordCount = 0;
        foreach (var element in profileElements.EnumerateArray())
        {
            var profile = ParseProfile(element, ref recordCount);
            var key = $"{profile.ProfileId:D}\u001f{profile.GameMode}\u001f{profile.ProfileGeneration}";
            if (!profileKeys.Add(key))
            {
                throw new InvalidDataException("Quest progress payload contains a duplicate profile scope.");
            }

            profiles.Add(profile);
        }

        var checksum = RequiredProperty(root, "checksum");
        RequireObject(checksum, "checksum");
        var algorithm = RequiredString(checksum, "algorithm", 32);
        if (!algorithm.Equals("sha256", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Quest progress checksum algorithm must be sha256.");
        }

        var expectedHash = RequiredString(checksum, "value", 128).ToLowerInvariant();
        if (expectedHash.Length != 64 || expectedHash.Any(value => !Uri.IsHexDigit(value)))
        {
            throw new InvalidDataException("Quest progress checksum must be a 64-character SHA-256 value.");
        }

        var normalizedProfiles = profiles
            .OrderBy(value => value.ProfileId)
            .ThenBy(value => value.GameMode)
            .ThenBy(value => value.ProfileGeneration, StringComparer.Ordinal)
            .ToArray();
        var actualHash = Sha256(CanonicalPayload(normalizedProfiles));
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expectedHash),
                Convert.FromHexString(actualHash)))
        {
            throw new InvalidDataException("Quest progress payload checksum does not match its normalized contents.");
        }

        return new(
            formatId,
            formatVersion,
            appVersion,
            exportedUtc,
            provenanceSummary,
            actualHash,
            normalizedProfiles);
    }

    private ProjectQuestProgressDocument ParseLegacyProfileSettings(JsonElement root)
    {
        RequireObject(root, "Profile settings envelope");
        var schemaVersion = RequiredInt32(root, "schemaVersion");
        if (schemaVersion != 1)
        {
            throw new InvalidDataException(
                "This is not project quest progress v2. Only the legacy profile settings schema 1 has a compatibility reader.");
        }

        var profileElement = RequiredProperty(root, "profile");
        RequireObject(profileElement, "legacy profile");
        var idText = RequiredString(profileElement, "id", 64);
        if (!Guid.TryParse(idText, out var profileId) || profileId == Guid.Empty)
        {
            throw new InvalidDataException("Legacy profile settings require a valid profile id.");
        }

        var profileName = RequiredString(profileElement, "name", 256);
        var gameMode = ParseGameMode(RequiredString(profileElement, "gameMode", 32));
        var recordCount = 0;
        var completedTaskIds = RequiredArray(profileElement, "completedTaskIds");
        var tasks = new List<ProjectQuestProgressTask>();
        var taskKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var taskElement in completedTaskIds.EnumerateArray())
        {
            CountRecord(ref recordCount);
            var taskId = RequiredStringValue(taskElement, "completedTaskIds entry", 256);
            RejectSurroundingWhitespace(taskId, "completedTaskIds entry");
            if (LooksSensitive(taskId))
            {
                throw new InvalidDataException("Legacy completed task id contains disallowed sensitive-looking data.");
            }

            if (!taskKeys.Add(taskId))
            {
                throw new InvalidDataException($"Legacy profile settings contain duplicate completed task id '{taskId}'.");
            }

            tasks.Add(new(taskId, RecordedTaskState.Completed));
        }

        var objectives = ParseLegacyCounts(
            RequiredProperty(profileElement, "objectiveProgress"),
            "objectiveProgress",
            ref recordCount,
            static (id, count) => new ProjectQuestProgressObjective(id, RecordedObjectiveState.InProgress, count));
        var holdings = ParseLegacyCounts(
            RequiredProperty(profileElement, "ownedItemCounts"),
            "ownedItemCounts",
            ref recordCount,
            static (id, count) => new ProjectQuestProgressHolding(id, false, checked((int)count)));
        var profile = new ProjectQuestProgressProfile(
            profileId,
            profileName,
            gameMode,
            $"legacy-{profileId:N}",
            tasks.OrderBy(value => value.TaskId, StringComparer.Ordinal).ToArray(),
            objectives.OrderBy(value => value.ObjectiveId, StringComparer.Ordinal).ToArray(),
            holdings.OrderBy(value => value.ItemId, StringComparer.Ordinal).ToArray(),
            []);
        var payloadHash = Sha256(CanonicalPayload([profile]));
        return new(
            "tarkov-companion.profile-settings",
            1,
            "legacy",
            DateTimeOffset.UnixEpoch,
            "Legacy project profile settings schema 1; only explicit completed tasks, objective counts, and non-FIR owned counts are retained.",
            payloadHash,
            [profile],
            true);
    }

    private IReadOnlyList<T> ParseLegacyCounts<T>(
        JsonElement element,
        string fieldName,
        ref int recordCount,
        Func<string, decimal, T> create)
    {
        RequireObject(element, fieldName);
        var values = new List<T>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            CountRecord(ref recordCount);
            var id = RequiredString(property.Name, $"{fieldName} key", 256);
            RejectSurroundingWhitespace(id, $"{fieldName} key");
            if (LooksSensitive(id))
            {
                throw new InvalidDataException($"Legacy profile field '{fieldName}' contains a sensitive-looking key.");
            }

            if (!keys.Add(id))
            {
                throw new InvalidDataException($"Legacy profile settings contain duplicate '{fieldName}' key '{id}'.");
            }

            if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetDecimal(out var count) || count < 0)
            {
                throw new InvalidDataException($"Legacy profile field '{fieldName}' requires finite nonnegative counts.");
            }

            if (fieldName == "ownedItemCounts" && (decimal.Truncate(count) != count || count > int.MaxValue))
            {
                throw new InvalidDataException("Legacy owned item counts must be nonnegative 32-bit integers.");
            }

            values.Add(create(id, count));
        }

        return values;
    }

    private ProjectQuestProgressProfile ParseProfile(JsonElement element, ref int recordCount)
    {
        RequireObject(element, "Quest progress profile");
        var idText = RequiredString(element, "profileId", 64);
        if (!Guid.TryParse(idText, out var profileId) || profileId == Guid.Empty)
        {
            throw new InvalidDataException("Quest progress profile id must be a non-empty UUID.");
        }

        var profileName = RequiredString(element, "profileName", 256);
        var mode = ParseGameMode(RequiredString(element, "gameMode", 32));
        var generation = RequiredString(element, "profileGeneration", 128);
        RejectSurroundingWhitespace(generation, "profile generation");
        if (LooksSensitive(generation))
        {
            throw new InvalidDataException("Quest progress profile generation contains disallowed sensitive-looking data.");
        }

        var tasks = ParseTasks(RequiredArray(element, "tasks"), ref recordCount);
        var objectives = ParseObjectives(RequiredArray(element, "objectives"), ref recordCount);
        var holdings = ParseHoldings(RequiredArray(element, "holdings"), ref recordCount);
        var pins = ParsePins(RequiredArray(element, "pins"), ref recordCount);
        return new(profileId, profileName, mode, generation, tasks, objectives, holdings, pins);
    }

    private IReadOnlyList<ProjectQuestProgressTask> ParseTasks(JsonElement array, ref int recordCount)
    {
        var values = new List<ProjectQuestProgressTask>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in array.EnumerateArray())
        {
            CountRecord(ref recordCount);
            RequireObject(element, "Task record");
            var id = RequiredIdentifier(element, "taskId");
            if (!keys.Add(id))
            {
                throw new InvalidDataException($"Quest progress profile contains duplicate task id '{id}'.");
            }

            values.Add(new(id, ParseEnum<RecordedTaskState>(element, "state")));
        }

        return values.OrderBy(value => value.TaskId, StringComparer.Ordinal).ToArray();
    }

    private IReadOnlyList<ProjectQuestProgressObjective> ParseObjectives(JsonElement array, ref int recordCount)
    {
        var values = new List<ProjectQuestProgressObjective>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in array.EnumerateArray())
        {
            CountRecord(ref recordCount);
            RequireObject(element, "Objective record");
            var id = RequiredIdentifier(element, "objectiveId");
            if (!keys.Add(id))
            {
                throw new InvalidDataException($"Quest progress profile contains duplicate objective id '{id}'.");
            }

            var state = ParseEnum<RecordedObjectiveState>(element, "state");
            var countElement = RequiredProperty(element, "count");
            decimal? count = countElement.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.Number when countElement.TryGetDecimal(out var parsed) => parsed,
                _ => throw new InvalidDataException("Objective count must be null or a finite decimal number."),
            };
            if (count is < 0)
            {
                throw new InvalidDataException("Objective count cannot be negative.");
            }

            if (state == RecordedObjectiveState.Unknown && count is not null)
            {
                throw new InvalidDataException("An unknown objective state cannot assert a progress count.");
            }

            values.Add(new(id, state, count));
        }

        return values.OrderBy(value => value.ObjectiveId, StringComparer.Ordinal).ToArray();
    }

    private IReadOnlyList<ProjectQuestProgressHolding> ParseHoldings(JsonElement array, ref int recordCount)
    {
        var values = new List<ProjectQuestProgressHolding>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in array.EnumerateArray())
        {
            CountRecord(ref recordCount);
            RequireObject(element, "Holding record");
            var id = RequiredIdentifier(element, "itemId");
            var foundInRaid = RequiredBoolean(element, "foundInRaid");
            var key = $"{id}\u001f{foundInRaid}";
            if (!keys.Add(key))
            {
                throw new InvalidDataException(
                    $"Quest progress profile contains duplicate holding '{id}' for foundInRaid={foundInRaid}.");
            }

            var count = RequiredInt32(element, "count");
            if (count < 0)
            {
                throw new InvalidDataException("Holding count cannot be negative.");
            }

            values.Add(new(id, foundInRaid, count));
        }

        return values
            .OrderBy(value => value.ItemId, StringComparer.Ordinal)
            .ThenBy(value => value.FoundInRaid)
            .ToArray();
    }

    private IReadOnlyList<ProjectQuestProgressPin> ParsePins(JsonElement array, ref int recordCount)
    {
        var values = new List<ProjectQuestProgressPin>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in array.EnumerateArray())
        {
            CountRecord(ref recordCount);
            RequireObject(element, "Pin record");
            var kind = ParseEnum<QuestPinTargetKind>(element, "targetKind");
            var id = RequiredIdentifier(element, "targetId");
            var key = $"{kind}\u001f{id}";
            if (!keys.Add(key))
            {
                throw new InvalidDataException($"Quest progress profile contains duplicate {kind} pin '{id}'.");
            }

            var sortOrder = RequiredInt32(element, "sortOrder");
            var noteElement = RequiredProperty(element, "note");
            var note = noteElement.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => noteElement.GetString(),
                _ => throw new InvalidDataException("Pin note must be a string or null."),
            };
            if (note is not null && LooksSensitive(note))
            {
                throw new InvalidDataException("Quest progress pin note contains disallowed sensitive-looking data.");
            }

            values.Add(new(kind, id, sortOrder, string.IsNullOrWhiteSpace(note) ? null : note));
        }

        return values
            .OrderBy(value => value.TargetKind)
            .ThenBy(value => value.TargetId, StringComparer.Ordinal)
            .ToArray();
    }

    private byte[] CanonicalPayload(IReadOnlyList<ProjectQuestProgressProfile> profiles)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("profiles");
            foreach (var profile in profiles
                         .OrderBy(value => value.ProfileId)
                         .ThenBy(value => value.GameMode)
                         .ThenBy(value => value.ProfileGeneration, StringComparer.Ordinal))
            {
                WriteProfile(writer, profile);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static void WriteProfile(Utf8JsonWriter writer, ProjectQuestProgressProfile profile)
    {
        writer.WriteStartObject();
        writer.WriteString("profileId", profile.ProfileId.ToString("D"));
        writer.WriteString("profileName", profile.ProfileName);
        writer.WriteString("gameMode", GameModeText(profile.GameMode));
        writer.WriteString("profileGeneration", profile.ProfileGeneration);
        writer.WriteStartArray("tasks");
        foreach (var task in profile.Tasks.OrderBy(value => value.TaskId, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("taskId", task.TaskId);
            writer.WriteString("state", EnumText(task.State));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("objectives");
        foreach (var objective in profile.Objectives.OrderBy(value => value.ObjectiveId, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("objectiveId", objective.ObjectiveId);
            writer.WriteString("state", EnumText(objective.State));
            if (objective.Count is { } count)
            {
                writer.WriteNumber("count", count);
            }
            else
            {
                writer.WriteNull("count");
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("holdings");
        foreach (var holding in profile.Holdings
                     .OrderBy(value => value.ItemId, StringComparer.Ordinal)
                     .ThenBy(value => value.FoundInRaid))
        {
            writer.WriteStartObject();
            writer.WriteString("itemId", holding.ItemId);
            writer.WriteBoolean("foundInRaid", holding.FoundInRaid);
            writer.WriteNumber("count", holding.Count);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("pins");
        foreach (var pin in profile.Pins
                     .OrderBy(value => value.TargetKind)
                     .ThenBy(value => value.TargetId, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("targetKind", EnumText(pin.TargetKind));
            writer.WriteString("targetId", pin.TargetId);
            writer.WriteNumber("sortOrder", pin.SortOrder);
            if (pin.Note is null)
            {
                writer.WriteNull("note");
            }
            else
            {
                writer.WriteString("note", pin.Note);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private async Task<byte[]> ReadBoundedAsync(string path, CancellationToken cancellationToken)
    {
        var maximumBytes = Positive(options.MaximumFileBytes, nameof(options.MaximumFileBytes));
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16_384,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > maximumBytes)
        {
            throw new InvalidDataException($"Quest progress import exceeds the {maximumBytes}-byte limit.");
        }

        using var output = new MemoryStream((int)Math.Min(stream.Length, maximumBytes));
        var buffer = ArrayPool<byte>.Shared.Rent(16_384);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (output.Length + read > maximumBytes)
                {
                    throw new InvalidDataException($"Quest progress import exceeds the {maximumBytes}-byte limit.");
                }

                output.Write(buffer, 0, read);
            }

            return output.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void ValidateAllStrings(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name.Length > 256)
                    {
                        throw new InvalidDataException("Quest progress import contains an overlong property name.");
                    }

                    ValidateAllStrings(property.Value);
                }

                break;
            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray())
                {
                    ValidateAllStrings(child);
                }

                break;
            case JsonValueKind.String:
                if ((element.GetString()?.Length ?? 0) > Positive(options.MaximumStringLength, nameof(options.MaximumStringLength)))
                {
                    throw new InvalidDataException(
                        $"Quest progress import contains a string longer than {options.MaximumStringLength} characters.");
                }

                break;
        }
    }

    private void ValidateProfileForWrite(ProjectQuestProgressProfile profile)
    {
        if (CountRecords(profile) > Positive(options.MaximumRecords, nameof(options.MaximumRecords)))
        {
            throw new InvalidOperationException("The profile contains too many quest progress records to export.");
        }

        if (string.IsNullOrWhiteSpace(profile.ProfileName) ||
            string.IsNullOrWhiteSpace(profile.ProfileGeneration) ||
            !profile.ProfileGeneration.Equals(profile.ProfileGeneration.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Quest progress export requires an exact profile name and generation.");
        }

        var ids = profile.Tasks.Select(value => value.TaskId)
                     .Concat(profile.Objectives.Select(value => value.ObjectiveId))
                     .Concat(profile.Holdings.Select(value => value.ItemId))
                     .Concat(profile.Pins.Select(value => value.TargetId));
        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id) || !id.Equals(id.Trim(), StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Quest progress source ids must be non-empty exact strings.");
            }

            EnsureSafeRequired(id, "source id");
            if (id.Length > 256)
            {
                throw new InvalidOperationException("Quest progress source ids cannot exceed 256 characters.");
            }
        }

        if (profile.ProfileName.Length > 256 || profile.ProfileGeneration.Length > 128 ||
            profile.Pins.Any(value => value.Note?.Length > options.MaximumStringLength))
        {
            throw new InvalidOperationException("Quest progress export contains an overlong owned string.");
        }

        if (profile.Objectives.Any(value => value.Count is < 0) || profile.Holdings.Any(value => value.Count < 0))
        {
            throw new InvalidOperationException("Quest progress export cannot contain negative counts.");
        }

        if (profile.Tasks.Any(value => !Enum.IsDefined(value.State)) ||
            profile.Objectives.Any(value => !Enum.IsDefined(value.State) ||
                value.State == RecordedObjectiveState.Unknown && value.Count is not null) ||
            profile.Pins.Any(value => !Enum.IsDefined(value.TargetKind)))
        {
            throw new InvalidOperationException("Quest progress export contains an unsupported state or value combination.");
        }

        if (profile.Tasks.Select(value => value.TaskId).Distinct(StringComparer.Ordinal).Count() != profile.Tasks.Count ||
            profile.Objectives.Select(value => value.ObjectiveId).Distinct(StringComparer.Ordinal).Count() != profile.Objectives.Count ||
            profile.Holdings.Select(value => (value.ItemId, value.FoundInRaid)).Distinct().Count() != profile.Holdings.Count ||
            profile.Pins.Select(value => (value.TargetKind, value.TargetId)).Distinct().Count() != profile.Pins.Count)
        {
            throw new InvalidOperationException("Quest progress export contains duplicate owned entity keys.");
        }
    }

    private static int CountRecords(ProjectQuestProgressProfile profile) =>
        checked(profile.Tasks.Count + profile.Objectives.Count + profile.Holdings.Count + profile.Pins.Count);

    private void CountRecord(ref int count)
    {
        count = checked(count + 1);
        if (count > Positive(options.MaximumRecords, nameof(options.MaximumRecords)))
        {
            throw new InvalidDataException(
                $"Quest progress import exceeds the {options.MaximumRecords}-record limit.");
        }
    }

    private static JsonElement RequiredProperty(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value)
            ? value
            : throw new InvalidDataException($"Quest progress import is missing required field '{name}'.");

    private static JsonElement RequiredArray(JsonElement parent, string name)
    {
        var value = RequiredProperty(parent, name);
        return value.ValueKind == JsonValueKind.Array
            ? value
            : throw new InvalidDataException($"Quest progress field '{name}' must be an array.");
    }

    private static string RequiredString(JsonElement parent, string name, int maximumLength) =>
        RequiredStringValue(RequiredProperty(parent, name), name, maximumLength);

    private static string RequiredStringValue(JsonElement element, string name, int maximumLength)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"Quest progress field '{name}' must be a string.");
        }

        return RequiredString(element.GetString(), name, maximumLength);
    }

    private static string RequiredString(string? value, string name, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
        {
            throw new InvalidDataException(
                $"Quest progress field '{name}' is required and cannot exceed {maximumLength} characters.");
        }

        return value;
    }

    private static string RequiredIdentifier(JsonElement parent, string name)
    {
        var value = RequiredString(parent, name, 256);
        RejectSurroundingWhitespace(value, name);
        if (LooksSensitive(value))
        {
            throw new InvalidDataException($"Quest progress field '{name}' contains disallowed sensitive-looking data.");
        }

        return value;
    }

    private static void RejectSurroundingWhitespace(string value, string name)
    {
        if (!value.Equals(value.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Quest progress field '{name}' cannot contain surrounding whitespace.");
        }
    }

    private static int RequiredInt32(JsonElement parent, string name)
    {
        var value = RequiredProperty(parent, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
        {
            throw new InvalidDataException($"Quest progress field '{name}' must be a 32-bit integer.");
        }

        return result;
    }

    private static bool RequiredBoolean(JsonElement parent, string name)
    {
        var value = RequiredProperty(parent, name);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException($"Quest progress field '{name}' must be a Boolean."),
        };
    }

    private static DateTimeOffset RequiredUtc(JsonElement parent, string name)
    {
        var value = RequiredProperty(parent, name);
        if (value.ValueKind != JsonValueKind.String || !value.TryGetDateTimeOffset(out var parsed) || parsed.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException($"Quest progress field '{name}' must be an explicit UTC timestamp.");
        }

        return parsed.ToUniversalTime();
    }

    private static T ParseEnum<T>(JsonElement parent, string name)
        where T : struct, Enum
    {
        var text = RequiredString(parent, name, 64);
        var canonicalName = Enum.GetNames<T>()
            .SingleOrDefault(value => value.Equals(text, StringComparison.OrdinalIgnoreCase));
        if (canonicalName is null || !Enum.TryParse<T>(canonicalName, ignoreCase: false, out var parsed))
        {
            throw new InvalidDataException($"Quest progress field '{name}' has unsupported value '{text}'.");
        }

        return parsed;
    }

    private static GameMode ParseGameMode(string value) => value switch
    {
        "regular" => GameMode.Regular,
        "pve" => GameMode.Pve,
        "pvpSeason" => GameMode.PvpSeason,
        _ => throw new InvalidDataException($"Unsupported quest progress game mode '{value}'."),
    };

    private static string GameModeText(GameMode mode) => mode switch
    {
        GameMode.Regular => "regular",
        GameMode.Pve => "pve",
        GameMode.PvpSeason => "pvpSeason",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static string EnumText<T>(T value)
        where T : struct, Enum
    {
        var text = value.ToString();
        return char.ToLowerInvariant(text[0]) + text[1..];
    }

    private static void RequireObject(JsonElement element, string description)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{description} must be a JSON object.");
        }
    }

    private static string RequiredPath(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        return Path.GetFullPath(filePath);
    }

    private static int Positive(int value, string name) => value > 0
        ? value
        : throw new ArgumentOutOfRangeException(name, "JSON resource limits must be positive.");

    private static string Sha256(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static string? SafeOptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value) || LooksSensitive(value) ? null : value;

    private static void EnsureSafeRequired(string value, string description)
    {
        if (LooksSensitive(value))
        {
            throw new InvalidOperationException(
                $"Quest progress export refused because the {description} resembles authorization data or an absolute local path.");
        }
    }

    private static bool LooksSensitive(string value)
    {
        var trimmed = value.Trim();
        return trimmed.StartsWith("/", StringComparison.Ordinal) ||
            ContainsUnixAbsolutePath(trimmed) ||
            (trimmed.Length >= 3 && char.IsLetter(trimmed[0]) && trimmed[1] == ':' &&
             (trimmed[2] == '\\' || trimmed[2] == '/')) ||
            trimmed.Contains("/home/", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("\\Users\\", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("authorization:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("bearer ", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("basic ", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("access_token", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("refresh_token", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("client_secret", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("api_token", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("api-token", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("account_id", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("account-id", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsUnixAbsolutePath(string value)
    {
        for (var index = 1; index < value.Length - 1; index++)
        {
            if (value[index] == '/' && char.IsWhiteSpace(value[index - 1]) &&
                !char.IsWhiteSpace(value[index + 1]) && value[index + 1] != '/')
            {
                return true;
            }
        }

        return false;
    }
}
