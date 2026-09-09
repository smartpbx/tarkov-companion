using System.Text.Json;
using TarkovCompanion.Application.Services;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.App.Services.Diagnostics;

public sealed record SyntheticRaidFixture(
    int SchemaVersion,
    string RaidId,
    DateTimeOffset StartedUtc,
    IReadOnlyList<SyntheticRaidEvent> Events);

public sealed record SyntheticRaidEvent(
    int Sequence,
    string Type,
    DateTimeOffset TimestampUtc,
    string Source,
    double Confidence,
    string? MapId = null,
    string? ScreenshotFilename = null,
    string? ItemId = null,
    IReadOnlyList<string>? ExtractIds = null,
    IReadOnlyList<string>? ContainerItemIds = null,
    string? Outcome = null);

public sealed record DemoRaidReplayReport(
    int SchemaVersion,
    string RaidId,
    string MapId,
    DateTimeOffset StartedUtc,
    DateTimeOffset EndedUtc,
    string Outcome,
    ScreenshotPosition LastPosition,
    IReadOnlyList<string> ScannedItemIds,
    IReadOnlyList<string> ActiveExtractIds,
    IReadOnlyList<string> ContainerItemIds,
    IReadOnlyList<string> EventTypes,
    bool Complete,
    bool UsesLiveDetection);

public static class DemoRaidReplay
{
    private static readonly string[] RequiredEventTypes =
    [
        "map",
        "position",
        "item",
        "extracts",
        "container",
        "raid-end",
    ];

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static string ResolveFixturePath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        var candidates = new[]
        {
            Path.Combine(Environment.CurrentDirectory, "fixtures", "simulator", "full-raid.json"),
            Path.Combine(AppContext.BaseDirectory, "fixtures", "simulator", "full-raid.json"),
        };

        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException(
                "The demo fixture was not found. Pass --demo-fixture <path>.",
                candidates[0]);
    }

    public static async Task<DemoRaidReplayReport> RunAsync(
        string fixturePath,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(fixturePath);
        var fixture = await JsonSerializer.DeserializeAsync<SyntheticRaidFixture>(stream, SerializerOptions, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("The synthetic raid fixture was empty.");

        ValidateFixture(fixture);

        string? mapId = null;
        ScreenshotPosition? lastPosition = null;
        var scannedItems = new List<string>();
        var extractIds = new List<string>();
        var containerItems = new List<string>();
        DateTimeOffset? endedUtc = null;
        string? outcome = null;

        foreach (var raidEvent in fixture.Events)
        {
            switch (raidEvent.Type)
            {
                case "map":
                    mapId = RequireValue(raidEvent.MapId, "mapId", raidEvent.Sequence);
                    break;
                case "position":
                    var filename = RequireValue(raidEvent.ScreenshotFilename, "screenshotFilename", raidEvent.Sequence);
                    if (!new ScreenshotFilenameParser().TryParse(filename, TimeSpan.Zero, out lastPosition))
                    {
                        throw new InvalidDataException($"Event {raidEvent.Sequence} has an invalid screenshot filename.");
                    }

                    break;
                case "item":
                    scannedItems.Add(RequireValue(raidEvent.ItemId, "itemId", raidEvent.Sequence));
                    break;
                case "extracts":
                    extractIds.AddRange(RequireValues(raidEvent.ExtractIds, "extractIds", raidEvent.Sequence));
                    break;
                case "container":
                    containerItems.AddRange(RequireValues(raidEvent.ContainerItemIds, "containerItemIds", raidEvent.Sequence));
                    break;
                case "raid-end":
                    outcome = RequireValue(raidEvent.Outcome, "outcome", raidEvent.Sequence);
                    endedUtc = raidEvent.TimestampUtc;
                    break;
                default:
                    throw new InvalidDataException($"Event {raidEvent.Sequence} has unsupported type '{raidEvent.Type}'.");
            }
        }

        return new(
            fixture.SchemaVersion,
            fixture.RaidId,
            mapId!,
            fixture.StartedUtc,
            endedUtc!.Value,
            outcome!,
            lastPosition!,
            scannedItems,
            extractIds,
            containerItems,
            [.. fixture.Events.Select(raidEvent => raidEvent.Type)],
            true,
            false);
    }

    public static async Task WriteAsync(
        DemoRaidReplayReport report,
        Stream destination,
        CancellationToken cancellationToken)
    {
        await JsonSerializer.SerializeAsync(destination, report, SerializerOptions, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateFixture(SyntheticRaidFixture fixture)
    {
        if (fixture.SchemaVersion != 1)
        {
            throw new InvalidDataException($"Unsupported synthetic raid schema version {fixture.SchemaVersion}.");
        }

        if (!Guid.TryParse(fixture.RaidId, out _))
        {
            throw new InvalidDataException("The synthetic raid ID must be a GUID.");
        }

        if (fixture.Events.Count != RequiredEventTypes.Length)
        {
            throw new InvalidDataException($"A full synthetic raid requires {RequiredEventTypes.Length} events.");
        }

        for (var index = 0; index < RequiredEventTypes.Length; index++)
        {
            var raidEvent = fixture.Events[index];
            if (raidEvent.Sequence != index + 1 || !string.Equals(raidEvent.Type, RequiredEventTypes[index], StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Event {index + 1} must be '{RequiredEventTypes[index]}' with matching sequence.");
            }

            if (raidEvent.TimestampUtc < fixture.StartedUtc
                || (index > 0 && raidEvent.TimestampUtc < fixture.Events[index - 1].TimestampUtc))
            {
                throw new InvalidDataException($"Event {raidEvent.Sequence} has a non-monotonic UTC timestamp.");
            }

            if (!string.Equals(raidEvent.Source, "simulator-fixture", StringComparison.Ordinal)
                || raidEvent.Confidence is < 0 or > 1)
            {
                throw new InvalidDataException($"Event {raidEvent.Sequence} has invalid evidence metadata.");
            }
        }
    }

    private static string RequireValue(string? value, string name, int sequence) =>
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidDataException($"Event {sequence} requires {name}.");

    private static IReadOnlyList<string> RequireValues(
        IReadOnlyList<string>? values,
        string name,
        int sequence) =>
        values is { Count: > 0 } && values.All(value => !string.IsNullOrWhiteSpace(value))
            ? values
            : throw new InvalidDataException($"Event {sequence} requires non-empty {name}.");
}
