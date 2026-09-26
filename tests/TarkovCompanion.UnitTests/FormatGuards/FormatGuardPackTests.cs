using System.Text.Json;
using TarkovCompanion.Application.Services;
using TarkovCompanion.Application.Services.FormatGuards;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Application.Services.Situations;
using TarkovCompanion.Core.Domain.Situations;

namespace TarkovCompanion.UnitTests.FormatGuards;

/// <summary>
/// [#712 0-3] Every log line and file-name shape the parsers depend on, per game build, through
/// every parser: a parser change or a new format shows up here as a named shape failing.
/// </summary>
/// <remarks>
/// <para>
/// The packs are <c>Packs/&lt;build&gt;/pack.json</c>. Each log line is offered to all six line
/// parsers and to the format-health shape checks, and a parser the entry does not name must answer
/// null: a shape that starts meaning something to a second parser is a change worth seeing.
/// </para>
/// <para>
/// Everything in a pack is synthetic: shapes measured on the owner's logs, values made up
/// (the repository is public). When a new build (Unity 6) changes a shape, add a folder for it
/// rather than editing the old one, so both keep being read.
/// </para>
/// </remarks>
public sealed class FormatGuardPackTests
{
    private static readonly DateTimeOffset Observed = new(2026, 1, 1, 20, 0, 0, TimeSpan.Zero);

    private static readonly Lazy<IReadOnlyList<Pack>> Packs = new(LoadPacks);

    public static TheoryData<string, string> LogLines() =>
        Data(pack => pack.Root.GetProperty("logLines"));

    public static TheoryData<string, string> ScreenshotNames() =>
        Data(pack => pack.Root.GetProperty("screenshotNames"));

    public static TheoryData<string, string> LogFiles() =>
        Data(pack => pack.Root.GetProperty("logFiles"));

    [Fact]
    public void EveryPackIsFoundAndNamesItsBuild()
    {
        Assert.Contains(Packs.Value, pack => pack.Folder == "1.1.5.1.47510");
        foreach (var pack in Packs.Value)
        {
            Assert.False(string.IsNullOrWhiteSpace(pack.Root.GetProperty("gameVersion").GetString()));
            var names = new[] { "logLines", "screenshotNames", "logFiles" }
                .SelectMany(section => pack.Root.GetProperty(section).EnumerateArray())
                .Select(entry => entry.GetProperty("name").GetString())
                .ToList();
            Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Theory]
    [MemberData(nameof(LogLines))]
    public void LogLineParsesAsItsPackSays(string folder, string name)
    {
        var entry = Entry(folder, "logLines", name);
        var line = entry.GetProperty("line").GetString()!;
        var file = $"2026.01.01_20-00-00_{folder} {entry.GetProperty("file").GetString()}_000.log";
        var expect = entry.GetProperty("expect");

        Assert.Equal(Enum.Parse<LineShape>(entry.GetProperty("header").GetString()!), EftLogLineShape.Header(line, EftLogLineShape.KindOf(file)));
        Assert.Equal(Enum.Parse<LineShape>(entry.GetProperty("notification").GetString()!), EftLogLineShape.Notification(line));

        var parser = new EftLogParser();
        if (Text(entry, "self") is { } self)
        {
            parser.RememberSelf(self);
        }

        var evidence = parser.ParseLine(line, Observed);
        if (expect.TryGetProperty("eftLog", out var raid))
        {
            Assert.NotNull(evidence);
            Assert.Equal(Text(raid, "state"), evidence.SuggestedState?.ToString());
            Assert.Equal(Text(raid, "map"), evidence.MapId);
            Assert.Equal(Text(raid, "raidKey"), evidence.RaidKey);
            Assert.Equal(Text(raid, "side"), evidence.Side);
            Assert.Equal(Text(raid, "eventId"), evidence.EventId);
            Assert.Equal(Flag(raid, "startsNewRaid"), evidence.StartsNewRaid);
            Assert.Equal(Flag(raid, "endsOnlyARaidWithoutId"), evidence.EndsOnlyARaidWithoutId);
        }
        else
        {
            Assert.Null(evidence);
        }

        var quest = QuestNotificationParser.ParseLine(line, Observed);
        if (expect.TryGetProperty("quest", out var questExpect))
        {
            Assert.NotNull(quest);
            Assert.Equal(Text(questExpect, "taskId"), quest.TaskId);
            Assert.Equal(Text(questExpect, "state"), quest.State.ToString());
        }
        else
        {
            Assert.Null(quest);
        }

        var sale = FleaSaleParser.ParseLine(line, Observed, TimeZoneInfo.Utc);
        if (expect.TryGetProperty("flea", out var saleExpect))
        {
            Assert.NotNull(sale);
            Assert.Equal(Text(saleExpect, "offerId"), sale.OfferId);
            Assert.Equal(Text(saleExpect, "handbookId"), sale.HandbookItemId);
            Assert.Equal(saleExpect.GetProperty("count").GetInt32(), sale.Count);
        }
        else
        {
            Assert.Null(sale);
        }

        var group = GroupNotificationParser.ParseLine(line, Observed);
        if (expect.TryGetProperty("group", out var groupExpect))
        {
            Assert.NotNull(group);
            Assert.Equal(Text(groupExpect, "kind"), group.Kind.ToString());
            if (Text(groupExpect, "nickname") is { } nickname)
            {
                Assert.Equal(nickname, group.Member?.Nickname);
            }
        }
        else
        {
            Assert.Null(group);
        }

        var loadTime = LoadTimeParser.ParseLine(line, Observed);
        if (expect.TryGetProperty("loadTimeSeconds", out var seconds))
        {
            Assert.NotNull(loadTime);
            Assert.Equal(seconds.GetDouble(), loadTime.RealSeconds, 3);
        }
        else
        {
            Assert.Null(loadTime);
        }

        Assert.Equal(Text(expect, "phase"), RaidPhaseMarkerParser.ParseLine(line, Observed)?.Kind.ToString());
        Assert.Equal(Text(expect, "sessionMode"), SessionModeParser.ParseLine(line, Observed)?.Mode?.ToString());
    }

    [Theory]
    [MemberData(nameof(ScreenshotNames))]
    public void ScreenshotNameParsesAsItsPackSays(string folder, string name)
    {
        var entry = Entry(folder, "screenshotNames", name);
        var file = entry.GetProperty("file").GetString()!;
        var kind = Enum.Parse<ScreenshotNameKind>(entry.GetProperty("kind").GetString()!);

        Assert.Equal(kind, ScreenshotFilenameParser.Classify(file));
        var parsed = new ScreenshotFilenameParser().TryParse(file, TimeSpan.Zero, out var position);
        Assert.Equal(kind == ScreenshotNameKind.InRaid, parsed);
        if (!parsed)
        {
            return;
        }

        Assert.NotNull(position);
        Assert.Equal(entry.GetProperty("x").GetDouble(), position.Position.X, 3);
        Assert.Equal(entry.GetProperty("y").GetDouble(), position.Position.Y, 3);
        Assert.Equal(entry.GetProperty("z").GetDouble(), position.Position.Z, 3);
        if (entry.TryGetProperty("heading", out var heading))
        {
            Assert.Equal(heading.GetDouble(), position.HeadingDegrees, 1);
        }

        Assert.Equal(
            entry.TryGetProperty("gameTimeSeconds", out var gameTime) ? gameTime.GetDouble() : null,
            position.InGameTime?.TotalSeconds);
        Assert.Equal(entry.TryGetProperty("duplicate", out var duplicate) ? duplicate.GetInt32() : null, position.DuplicateIndex);
    }

    [Theory]
    [MemberData(nameof(LogFiles))]
    public void LogFileNameIsReadAsItsPackSays(string folder, string name)
    {
        var entry = Entry(folder, "logFiles", name);
        var file = entry.GetProperty("file").GetString()!;

        Assert.Equal(Text(entry, "version"), EftLogFolderGameVersionSource.ParseFolderName(Text(entry, "folder")));
        Assert.Equal(Text(entry, "readMode"), EftLogFiles.ReadMode(file)?.ToString());
        Assert.Equal(Text(entry, "kind"), EftLogLineShape.KindOf(file).ToString());
    }

    /// <summary>Each build's lines, as a session would deliver them, leave that build's log format OK.</summary>
    [Fact]
    public void EveryPacksLinesReadAsHealthy()
    {
        foreach (var pack in Packs.Value)
        {
            var monitor = new FormatHealthMonitor(windows: new Dictionary<FormatSource, FormatWindow>
            {
                [FormatSource.GameLog] = new(200, 1, 0.5, 0.8),
                [FormatSource.Notification] = new(20, 1, 0.5, 0.8),
                [FormatSource.ScreenshotName] = new(10, 1, 0.5, 0.8),
            });
            foreach (var entry in pack.Root.GetProperty("logLines").EnumerateArray())
            {
                monitor.ObserveLogLine(
                    $"log_2026.01.01_20-00-00_{pack.Folder}/2026.01.01_20-00-00_{pack.Folder} {Text(entry, "file")}_000.log",
                    Text(entry, "line"));
            }

            foreach (var entry in pack.Root.GetProperty("screenshotNames").EnumerateArray())
            {
                monitor.ObserveScreenshotName(Text(entry, "file")!);
            }

            Assert.False(monitor.Current.IsDegraded, pack.Folder);
            Assert.Equal(FormatHealthStatus.Ok, monitor.Current.For(FormatSource.GameLog).Status);
        }
    }

    private static TheoryData<string, string> Data(Func<Pack, JsonElement> section)
    {
        var data = new TheoryData<string, string>();
        foreach (var pack in Packs.Value)
        {
            foreach (var entry in section(pack).EnumerateArray())
            {
                data.Add(pack.Folder, entry.GetProperty("name").GetString()!);
            }
        }

        return data;
    }

    private static JsonElement Entry(string folder, string section, string name) =>
        Packs.Value.Single(pack => pack.Folder == folder).Root.GetProperty(section).EnumerateArray()
            .Single(entry => entry.GetProperty("name").GetString() == name);

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Flag(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static IReadOnlyList<Pack> LoadPacks()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "FormatGuards", "Packs");
        return [.. Directory.EnumerateFiles(root, "pack.json", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(path => new Pack(
                Path.GetFileName(Path.GetDirectoryName(path)!),
                JsonDocument.Parse(File.ReadAllText(path)).RootElement))];
    }

    private sealed record Pack(string Folder, JsonElement Root);
}
