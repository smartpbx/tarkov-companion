using System.Globalization;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.Core.Common;

using TarkovCompanion.Application.Services;
using TarkovCompanion.App.Localization;

namespace TarkovCompanion.App.Services.V2.SelfTest;

/// <summary>
/// Turns measured readings into the report, one capability at a time.
/// </summary>
/// <remarks>
/// Pure functions over records, so every pass, fail and unknown shape is a test rather than a
/// thing somebody has to reproduce on a Windows machine with the game running. The adapter that
/// takes the readings is the only part that needs a real installation.
///
/// Two rules hold across all seven. Every line names what it was read from, because "checked"
/// with no source is what let a stale folder and an unnamed endpoint failure both go unnoticed
/// for a day. And a capability that could not be exercised returns
/// <see cref="SelfTestOutcome.Unknown"/> with the reason, never a pass.
/// </remarks>
public static class SelfTestProbes
{
    public const string FoldersId = "game-folders";
    public const string LogsId = "game-logs";
    public const string ScreenshotsId = "screenshots";
    public const string GameDataId = "game-data";
    public const string DatabaseId = "database";
    public const string RelayId = "relay";
    public const string TabletId = "tablet";

    /// <summary>
    /// How far behind the screenshots a log folder may fall before it is called stale.
    /// </summary>
    /// <remarks>
    /// #414: the companion watched a log folder the game had stopped writing to, and every
    /// screenshot still arrived, so nothing looked wrong until a whole evening's raids were
    /// missing from History. Screenshots arriving while the logs stand still is the signature.
    /// </remarks>
    public static readonly TimeSpan LogFolderStaleAfter = TimeSpan.FromHours(1);

    public static SelfTestCapability Folders(
        SelfTestFolders reading,
        DateTimeOffset nowUtc,
        TimeSpan took,
        CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(culture);
        var source = ReadAt(reading.CheckedUtc, SetupText.ProbeFromDiscovery, culture);
        if (!reading.Supported || reading.Problem is { Length: > 0 })
        {
            // The detail is discovery's own sentence about why; the problem is the shorter
            // fact under it. Using the problem as the headline printed a fragment.
            return Unknown(
                FoldersId,
                SetupText.ProbeTitleGameFolders,
                reading.Detail,
                [new(reading.Problem ?? reading.Detail, source)],
                took);
        }

        var facts = new List<SelfTestFact>(reading.Folders.Count + 1);
        var broken = new List<string>();
        foreach (var folder in reading.Folders)
        {
            var changed = folder.ChangedUtc is { } at
                ? SetupText.ProbeFolderLastChanged(culture, V2ShellText.Age(at, nowUtc, culture), LocalTime.Sortable(at))
                : SetupText.ProbeFolderNeverChanged;
            if (folder.Path is null || !folder.Exists)
            {
                broken.Add(SetupText.ProbePurpose(folder.Purpose));
                facts.Add(new(
                    SetupText.ProbeFolderBroken(culture, SetupText.ProbePurpose(folder.Purpose), folder.Problem ?? SetupText.ProbeFolderNotFound, folder.Why),
                    source));
                continue;
            }

            facts.Add(new(
                SetupText.ProbeFolderChosen(culture, SetupText.ProbePurpose(folder.Purpose), folder.Path, folder.Why, folder.Entries, changed),
                source));
        }

        string? stale = null;
        if (StaleLogFolder(reading, out var logChanged, out var screenshotChanged))
        {
            stale = SetupText.ProbeFolderStaleFact(culture, LocalTime.Sortable(logChanged), LocalTime.Sortable(screenshotChanged));
            facts.Add(new(stale, source));
        }

        if (stale is null && broken.Count == 0)
        {
            return new(
                FoldersId,
                SetupText.ProbeTitleGameFolders,
                SelfTestOutcome.Pass,
                SetupText.ProbeFoldersPass(culture, reading.Folders.Count),
                facts,
                took);
        }

        // The headline is the problem, not a list of names. A player reading "Logs is not usable"
        // still has to go and find out in what way.
        return new(
            FoldersId,
            SetupText.ProbeTitleGameFolders,
            SelfTestOutcome.Fail,
            broken.Count > 0
                ? SetupText.ProbeFoldersBroken(culture, string.Join(SetupText.ProbeFoldersJoin, broken.Distinct(StringComparer.Ordinal)).ToLowerInvariant())
                : SetupText.ProbeFoldersStale,
            facts,
            took);
    }

    private static bool StaleLogFolder(
        SelfTestFolders reading,
        out DateTimeOffset logChanged,
        out DateTimeOffset screenshotChanged)
    {
        logChanged = default;
        screenshotChanged = default;
        var logs = reading.Folders.FirstOrDefault(folder => folder.Purpose == SelfTestFolderPurpose.Logs);
        var shots = reading.Folders.FirstOrDefault(folder => folder.Purpose == SelfTestFolderPurpose.Screenshots);
        if (logs is not { Exists: true, ChangedUtc: { } logAt } || shots is not { Exists: true, ChangedUtc: { } shotAt })
        {
            return false;
        }

        logChanged = logAt;
        screenshotChanged = shotAt;
        return shotAt - logAt > LogFolderStaleAfter;
    }

    public static SelfTestCapability Logs(
        SelfTestLogs reading,
        DateTimeOffset nowUtc,
        TimeSpan took,
        CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(culture);
        var source = ReadAt(reading.ReadUtc, SetupText.ProbeFromLogFiles, culture);
        if (reading.Problem is { Length: > 0 } problem)
        {
            return Unknown(LogsId, SetupText.ProbeTitleLogs, SetupText.ProbeLogsNothingRead(problem), [], took);
        }

        var facts = new List<SelfTestFact>(8)
        {
            new(
                SetupText.ProbeLogsRead(culture, reading.LinesRead, Bytes(reading.Bytes, culture), reading.Files.Count, reading.SessionFolder),
                source),
        };

        // Per file, because the whole reason a raid's quests went unnoticed for a day is that a
        // reading like this one looked at a single file and reported its zero as the session's.
        foreach (var file in reading.Files)
        {
            facts.Add(new(
                file.Problem is { Length: > 0 } unreadable
                    ? SetupText.ProbeLogsFileUnreadable(culture, file.Name, unreadable)
                    : SetupText.ProbeLogsFile(culture, file.Name, file.LinesRead, file.Mode == LogReadMode.ChatOnly ? SetupText.ProbeLogsChatOnly : string.Empty, file.QuestEvents, file.FleaSales),
                source));
        }

        if (reading.SkippedFiles.Count > 0)
        {
            facts.Add(new(
                SetupText.ProbeLogsNotOpened(culture, string.Join(", ", reading.SkippedFiles)),
                SetupText.ProbeSourceReadsOnlyUsed));
        }

        if (reading.SessionStartedUtc is { } started)
        {
            facts.Add(new(
                SetupText.ProbeLogsSessionStarted(culture, V2ShellText.Age(started, nowUtc, culture), LocalTime.Sortable(started)),
                SetupText.ProbeSourceSessionFolderName));
        }

        facts.Add(new(
            reading.RaidsSeen == 0
                ? SetupText.ProbeLogsNoRaid
                : SetupText.ProbeLogsRaids(culture, reading.RaidsSeen, reading.LastRaidMap ?? SetupText.ProbeLogsUnnamedMap, reading.LastRaidState),
            source));
        if (reading.LastRaidAtUtc is { } lastRaid)
        {
            facts.Add(new(
                SetupText.ProbeLogsLastRaidLine(culture, V2ShellText.Age(lastRaid, nowUtc, culture), LocalTime.Sortable(lastRaid)),
                source));
        }

        facts.Add(new(
            reading.QueueTime is { } queue
                ? SetupText.ProbeLogsQueueTime(culture, queue.TotalSeconds)
                : SetupText.ProbeLogsNoQueueTime,
            source));
        facts.Add(new(
            SetupText.ProbeLogsEvents(culture, reading.QuestEvents, reading.FleaSales),
            source));

        var understood = reading.RaidsSeen + reading.QuestEvents + reading.FleaSales;
        if (reading.LinesRead == 0)
        {
            return new(
                LogsId,
                SetupText.ProbeTitleLogs,
                SelfTestOutcome.Fail,
                SetupText.ProbeLogsEmpty(culture, reading.FileName, Bytes(reading.Bytes, culture)),
                facts,
                took);
        }

        if (understood == 0 && reading.QueueTime is null)
        {
            return Unknown(
                LogsId,
                SetupText.ProbeTitleLogs,
                SetupText.ProbeLogsNothingRecognised(culture, reading.LinesRead),
                facts,
                took);
        }

        // A session with raids in it and no quest notification anywhere used to pass quietly,
        // which is how a player who had handed in quests came to believe the companion had seen
        // them. It reads a raid and misses every quest for one reason -- the quest announcements
        // are not in anything being read -- and that has to be on the face of the report.
        return reading.QuestEvents == 0 && reading.RaidsSeen > 0
            ? Unknown(
                LogsId,
                SetupText.ProbeTitleLogs,
                SetupText.ProbeLogsNoQuests(culture, reading.RaidsSeen, reading.Files.Count),
                facts,
                took)
            : new(
                LogsId,
                SetupText.ProbeTitleLogs,
                SelfTestOutcome.Pass,
                SetupText.ProbeLogsPass(culture, understood),
                facts,
                took);
    }

    /// <summary>
    /// What one screenshot proved, or honestly could not.
    /// </summary>
    /// <remarks>
    /// [V2 rough package 43a] Two reports from Clayton shaped every branch below. The probe failed
    /// "only because i cant alt tab back to the game and screenshot fast enough" — so a shot he had
    /// already taken counts, and running out of patience is never a fault. And it called
    /// <c>2026-09-18[19-03]_19.67 (1).png</c> broken, which is a post-raid screenshot the game
    /// writes without a position on purpose — so "no position in the name" is only a fault when the
    /// name says the shot was taken in a raid.
    /// </remarks>
    public static SelfTestCapability Screenshots(
        SelfTestScreenshot reading,
        TimeSpan took,
        CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(culture);
        if (reading.Problem is { Length: > 0 } problem)
        {
            return Unknown(ScreenshotsId, SetupText.ProbeTitleScreenshots, problem, [], took);
        }

        if (reading.FileName is null)
        {
            // Never a failure. Nobody took a screenshot, which is a fact about the evening rather
            // than about this installation, and the difference is what this page is for.
            return Unknown(
                ScreenshotsId,
                SetupText.ProbeTitleScreenshots,
                SetupText.ProbeShotsNoneTaken(culture, reading.Waited.TotalMinutes),
                [new(
                    SetupText.ProbeShotsWatched(culture, reading.Root, reading.Waited.TotalMinutes),
                    SetupText.ProbeSourceScreenshotFolderListed)],
                took);
        }

        var noticed = reading.NoticedUtc ?? default;
        var source = ReadAt(noticed, SetupText.ProbeFromScreenshotFile, culture);
        var facts = new List<SelfTestFact>(5)
        {
            new(
                reading.WasAlreadyThere
                    ? SetupText.ProbeShotsUsedExisting(culture, reading.FileName)
                    : SetupText.ProbeShotsAppeared(culture, reading.FileName, reading.Waited.TotalSeconds),
                source),
        };
        if (reading.WasAlreadyThere && reading.Age is { } age)
        {
            facts.Add(new(
                SetupText.ProbeShotsTakenBefore(culture, age.TotalMinutes),
                source));
        }

        if (reading.WrittenUtc is { } written)
        {
            facts.Add(new(
                SetupText.ProbeShotsWritten(culture, LocalTime.SortableSeconds(written), reading.Clock),
                source));
        }

        if (reading.EndToEnd is { } endToEnd)
        {
            facts.Add(new(
                reading.WasAlreadyThere
                    ? SetupText.ProbeShotsEndToEndExisting(culture, endToEnd.TotalMilliseconds)
                    : SetupText.ProbeShotsEndToEnd(culture, endToEnd.TotalMilliseconds),
                source));
        }

        if (!reading.Parsed)
        {
            // The game only writes the coordinate and rotation blocks for a shot taken in a raid.
            // A menu, hideout or post-raid screenshot has nowhere for a position to be, so reading
            // none out of it is the parser working, not failing.
            if (reading.NameKind != ScreenshotNameKind.InRaid)
            {
                facts.Add(new(
                    SetupText.ProbeShotsNoCoordinates,
                    source));
                return Unknown(
                    ScreenshotsId,
                    SetupText.ProbeTitleScreenshots,
                    SetupText.ProbeShotsOutsideRaid,
                    facts,
                    took);
            }

            facts.Add(new(
                SetupText.ProbeShotsInRaidNoPosition,
                source));
            return new(
                ScreenshotsId,
                SetupText.ProbeTitleScreenshots,
                SelfTestOutcome.Fail,
                SetupText.ProbeShotsFail,
                facts,
                took);
        }

        facts.Add(new(
            SetupText.ProbeShotsPosition(culture, reading.X, reading.Y, reading.Z),
            source));
        return new(
            ScreenshotsId,
            SetupText.ProbeTitleScreenshots,
            SelfTestOutcome.Pass,
            reading.WasAlreadyThere
                ? SetupText.ProbeShotsPassExisting(culture, reading.EndToEnd?.TotalMilliseconds ?? 0)
                : SetupText.ProbeShotsPass(culture, reading.EndToEnd?.TotalMilliseconds ?? 0),
            facts,
            took);
    }

    public static SelfTestCapability GameData(
        SelfTestGameData reading,
        DateTimeOffset nowUtc,
        TimeSpan took,
        CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(culture);
        var source = ReadAt(reading.ReadUtc, SetupText.ProbeFromSyncRecord, culture);
        if (reading.Problem is { Length: > 0 } problem)
        {
            return Unknown(GameDataId, SetupText.ProbeTitleGameData, problem, [], took);
        }

        if (reading.Endpoints.Count == 0)
        {
            return Unknown(
                GameDataId,
                SetupText.ProbeTitleGameData,
                SetupText.ProbeDataNoEndpoint,
                [new(SetupText.ProbeDataNoRows(reading.GameMode, reading.Language), source)],
                took);
        }

        var facts = new List<SelfTestFact>(reading.Endpoints.Count);
        var broken = new List<string>();
        foreach (var endpoint in reading.Endpoints)
        {
            var age = endpoint.RefreshedUtc is { } at
                ? V2ShellText.Age(at, nowUtc, culture)
                : SetupText.ProbeDataNeverRefreshed;
            if (endpoint.Error is { Length: > 0 } error)
            {
                broken.Add(endpoint.Name);
                facts.Add(new(
                    SetupText.ProbeDataDidNotRefresh(culture, endpoint.Name, error, age),
                    source));
                continue;
            }

            if (endpoint.Rows is 0 or null)
            {
                broken.Add(endpoint.Name);
                facts.Add(new(
                    SetupText.ProbeDataNoRowsLanded(culture, endpoint.Name, Bytes(endpoint.Bytes, culture), age),
                    source));
                continue;
            }

            facts.Add(new(
                SetupText.ProbeDataRows(culture, endpoint.Name, endpoint.Rows, Bytes(endpoint.Bytes, culture), age),
                source));
        }

        return broken.Count > 0
            ? new(
                GameDataId,
                SetupText.ProbeTitleGameData,
                SelfTestOutcome.Fail,
                SetupText.ProbeDataFail(culture, string.Join(", ", broken)),
                facts,
                took)
            : new(
                GameDataId,
                SetupText.ProbeTitleGameData,
                SelfTestOutcome.Pass,
                SetupText.ProbeDataPass(culture, reading.Endpoints.Count),
                facts,
                took);
    }

    public static SelfTestCapability Database(
        SelfTestDatabase reading,
        TimeSpan took,
        CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(culture);
        if (reading.Problem is { Length: > 0 } problem || reading.Path is null)
        {
            return Unknown(DatabaseId, SetupText.ProbeTitleDatabase, reading.Problem ?? SetupText.ProbeDbNone, [], took);
        }

        var source = ReadAt(reading.ReadUtc, SetupText.ProbeFromDatabase, culture);
        var missing = reading.Expected.Except(reading.Applied, StringComparer.Ordinal).ToArray();
        var facts = new List<SelfTestFact>(reading.Tables.Count + 2)
        {
            new(string.Create(culture, $"{reading.Path} — {Bytes(reading.Bytes, culture)}"), source),
            new(
                missing.Length == 0
                    ? SetupText.ProbeDbMigrationsApplied(culture, reading.Expected.Count, reading.Applied.LastOrDefault() ?? SetupText.ProbeDbNoMigration)
                    : SetupText.ProbeDbMigrationsMissing(culture, missing.Length, string.Join(", ", missing)),
                SetupText.ProbeSourceMigrationsTable),
        };
        facts.AddRange(reading.Tables.Select(table => new SelfTestFact(
            SetupText.ProbeDbTableRows(culture, table.Name, table.Rows),
            source)));

        if (missing.Length > 0)
        {
            return new(
                DatabaseId,
                SetupText.ProbeTitleDatabase,
                SelfTestOutcome.Fail,
                SetupText.ProbeDbBehind(culture, missing.Length),
                facts,
                took);
        }

        return reading.Bytes == 0
            ? new(DatabaseId, SetupText.ProbeTitleDatabase, SelfTestOutcome.Fail, SetupText.ProbeDbEmpty, facts, took)
            : new(
                DatabaseId,
                SetupText.ProbeTitleDatabase,
                SelfTestOutcome.Pass,
                SetupText.ProbeDbPass(culture, Bytes(reading.Bytes, culture), reading.Tables.Count),
                facts,
                took);
    }

    public static SelfTestCapability Relay(
        SelfTestRelay reading,
        DateTimeOffset nowUtc,
        TimeSpan took,
        CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(culture);
        if (!reading.Configured)
        {
            return Unknown(
                RelayId,
                SetupText.ProbeTitleRelay,
                SetupText.ProbeRelayNotConfigured,
                [new(SetupText.ProbeRelaySharingOffNoServer, SetupText.ProbeSourceGroupSettings)],
                took);
        }

        var source = ReadAt(reading.ReadUtc, SetupText.ProbeFromRelayHealth, culture);
        if (!reading.Reachable)
        {
            return new(
                RelayId,
                SetupText.ProbeTitleRelay,
                SelfTestOutcome.Fail,
                SetupText.ProbeRelayNoAnswer(culture, reading.Origin),
                [new(reading.Problem ?? SetupText.ProbeRelayHealthMissing, source)],
                took);
        }

        var facts = new List<SelfTestFact>(6)
        {
            new(
                SetupText.ProbeRelayAnswered(culture, reading.Origin, reading.RoundTrip?.TotalMilliseconds ?? 0),
                source),
            new(
                SetupText.ProbeRelayRunning(culture, reading.Version ?? SetupText.ProbeRelayUnnamedBuild, reading.Commit is null ? string.Empty : $" ({reading.Commit})", reading.Protocol?.ToString(culture) ?? SetupText.ProbeRelayProtocolUnknown),
                source),
            new(
                SetupText.ProbeRelayRooms(culture, reading.Rooms ?? 0, reading.Members ?? 0),
                source),
            new(
                reading.Sharing
                    ? SetupText.ProbeRelaySharingOn(culture, reading.MyName ?? SetupText.ProbeRelayUnnamedPlayer, reading.Others.Count)
                    : SetupText.ProbeRelaySharingOff,
                SetupText.ProbeSourceGroupSettingsAndExchange),
        };

        // Package 31's measurement where there is one: what the last few screenshots took to
        // arrive, not how old one marker happens to be. Where nothing has been delivered yet,
        // the freshest marker's age is the honest second best, and where there is not even one
        // of those this says so rather than printing a zero.
        var latency = reading.PositionLatency;
        var freshest = reading.Others
            .Where(other => other.PositionAge is not null)
            .OrderBy(other => other.PositionAge!.Value)
            .FirstOrDefault();
        facts.Add(latency.HasSamples
            ? new(
                SetupText.ProbeRelayLatency(culture, latency.Median.TotalSeconds, latency.Slowest95.TotalSeconds, latency.SampleCount, latency.Delivered),
                SetupText.ProbeSourceLatencyTiming)
            : new(
                freshest is { PositionAge: { } age }
                    ? SetupText.ProbeRelayFreshest(culture, age.TotalSeconds, freshest.Name)
                    : reading.Others.Count == 0
                        ? SetupText.ProbeRelayNoSquadmate
                        : SetupText.ProbeRelayNoPosition,
                SetupText.ProbeSourceLastExchange));

        if (reading.StaleSinceUtc is { } stale)
        {
            facts.Add(new(
                SetupText.ProbeRelayStale(culture, V2ShellText.Age(stale, nowUtc, culture)),
                source));
            return new(
                RelayId,
                SetupText.ProbeTitleRelay,
                SelfTestOutcome.Fail,
                SetupText.ProbeRelayExchangeStopped,
                facts,
                took);
        }

        return new(
            RelayId,
            SetupText.ProbeTitleRelay,
            SelfTestOutcome.Pass,
            SetupText.ProbeRelayPass(culture, reading.RoundTrip?.TotalMilliseconds ?? 0),
            facts,
            took);
    }

    public static SelfTestCapability Tablet(
        SelfTestTablet reading,
        DateTimeOffset nowUtc,
        TimeSpan took,
        CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(culture);
        if (!reading.Supported || reading.Problem is { Length: > 0 })
        {
            return Unknown(
                TabletId,
                SetupText.ProbeTitleTablet,
                reading.Problem ?? SetupText.ProbeTabletUnsupported,
                [],
                took);
        }

        var source = ReadAt(reading.ReadUtc, SetupText.ProbeFromPairedDevices, culture);
        var facts = new List<SelfTestFact>(reading.Devices.Count + 2);
        foreach (var device in reading.Devices)
        {
            facts.Add(new(
                SetupText.ProbeTabletDevice(culture, device.Name, device.Role, device.Status, V2ShellText.Age(device.LastSeenUtc, nowUtc, culture)),
                source));
        }

        if (reading.Devices.Count == 0)
        {
            return Unknown(
                TabletId,
                SetupText.ProbeTitleTablet,
                SetupText.ProbeTabletNoDevice,
                [new(SetupText.ProbeTabletNoDevicesAt(reading.Origin ?? SetupText.ProbeTabletThisRelay), source)],
                took);
        }

        facts.Add(new(
            reading.PublishedUtc is { } published
                ? SetupText.ProbeTabletPublished(culture, reading.MapName ?? SetupText.ProbeTabletAMap, reading.Objects, V2ShellText.Age(published, nowUtc, culture))
                : SetupText.ProbeTabletNoScene,
            SetupText.ProbeSourceTabletPublisher));

        return reading.Publishing
            ? new(
                TabletId,
                SetupText.ProbeTitleTablet,
                SelfTestOutcome.Pass,
                SetupText.ProbeTabletPass(culture, reading.Devices.Count),
                facts,
                took)
            : new(
                TabletId,
                SetupText.ProbeTitleTablet,
                SelfTestOutcome.Fail,
                SetupText.ProbeTabletFail(culture, reading.Devices.Count),
                facts,
                took);
    }

    private static SelfTestCapability Unknown(
        string id,
        string title,
        string headline,
        IReadOnlyList<SelfTestFact> facts,
        TimeSpan took) =>
        new(id, title, SelfTestOutcome.Unknown, headline, facts, took);

    private static string ReadAt(DateTimeOffset at, string what, CultureInfo culture) =>
        SetupText.ProbeReadAt(culture, what, LocalTime.Time(at, culture));

    private static string Bytes(long bytes, CultureInfo culture) => bytes switch
    {
        < 0 => SetupText.ProbeBytesUnknown,
        < 1024 => SetupText.ProbeBytesB(culture, bytes),
        < 1024 * 1024 => SetupText.ProbeBytesKB(culture, bytes / 1024.0),
        < 1024L * 1024 * 1024 => SetupText.ProbeBytesMB(culture, bytes / (1024.0 * 1024)),
        _ => SetupText.ProbeBytesGB(culture, bytes / (1024.0 * 1024 * 1024)),
    };
}
