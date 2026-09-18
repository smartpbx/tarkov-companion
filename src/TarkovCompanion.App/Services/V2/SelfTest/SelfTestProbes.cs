using System.Globalization;
using TarkovCompanion.App.Services.V2.Shell;

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
        var source = ReadAt(reading.CheckedUtc, "discovery", culture);
        if (!reading.Supported || reading.Problem is { Length: > 0 })
        {
            // The detail is discovery's own sentence about why; the problem is the shorter
            // fact under it. Using the problem as the headline printed a fragment.
            return Unknown(
                FoldersId,
                "Game folders",
                reading.Detail,
                [new(reading.Problem ?? reading.Detail, source)],
                took);
        }

        var facts = new List<SelfTestFact>(reading.Folders.Count + 1);
        var broken = new List<string>();
        foreach (var folder in reading.Folders)
        {
            var changed = folder.ChangedUtc is { } at
                ? string.Create(culture, $"last changed {V2ShellText.Age(at, nowUtc, culture)} ({at:yyyy-MM-dd HH:mm} UTC)")
                : "nothing in it has ever changed";
            if (folder.Path is null || !folder.Exists)
            {
                broken.Add(folder.Purpose);
                facts.Add(new(
                    string.Create(culture, $"{folder.Purpose}: {folder.Problem ?? "not found"} — looked because {folder.Why}"),
                    source));
                continue;
            }

            facts.Add(new(
                string.Create(culture, $"{folder.Purpose}: {folder.Path} — chosen because {folder.Why}; {folder.Entries:N0} entries, {changed}"),
                source));
        }

        string? stale = null;
        if (StaleLogFolder(reading, out var logChanged, out var screenshotChanged))
        {
            stale = string.Create(
                culture,
                $"The log folder has stood still since {logChanged:yyyy-MM-dd HH:mm} UTC while screenshots kept arriving until {screenshotChanged:yyyy-MM-dd HH:mm} UTC — the game is writing its logs somewhere else.");
            facts.Add(new(stale, source));
        }

        if (stale is null && broken.Count == 0)
        {
            return new(
                FoldersId,
                "Game folders",
                SelfTestOutcome.Pass,
                string.Create(culture, $"{reading.Folders.Count} folder(s) found, and each one is being written to."),
                facts,
                took);
        }

        // The headline is the problem, not a list of names. A player reading "Logs is not usable"
        // still has to go and find out in what way.
        return new(
            FoldersId,
            "Game folders",
            SelfTestOutcome.Fail,
            broken.Count > 0
                ? string.Create(culture, $"The {string.Join(" and ", broken.Distinct(StringComparer.Ordinal)).ToLowerInvariant()} folder could not be used.")
                : "The log folder has stopped changing while screenshots keep arriving.",
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
        var logs = reading.Folders.FirstOrDefault(folder => folder.Purpose == "Logs");
        var shots = reading.Folders.FirstOrDefault(folder => folder.Purpose == "Screenshots");
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
        var source = ReadAt(reading.ReadUtc, "the game's own log files", culture);
        if (reading.Problem is { Length: > 0 } problem)
        {
            return Unknown(LogsId, "Logs", $"Nothing was read: {problem}", [], took);
        }

        var facts = new List<SelfTestFact>(6)
        {
            new(
                string.Create(
                    culture,
                    $"Read {reading.LinesRead:N0} line(s), {Bytes(reading.Bytes, culture)}, from {reading.FileName} in session {reading.SessionFolder}"),
                source),
        };

        if (reading.SessionStartedUtc is { } started)
        {
            facts.Add(new(
                string.Create(culture, $"That session started {V2ShellText.Age(started, nowUtc, culture)} ({started:yyyy-MM-dd HH:mm} UTC)"),
                "the session folder's own name"));
        }

        facts.Add(new(
            reading.RaidsSeen == 0
                ? "No raid was recognised in this session"
                : string.Create(culture, $"{reading.RaidsSeen} raid(s) recognised; the last was {reading.LastRaidMap ?? "on an unnamed map"} ({reading.LastRaidState})"),
            source));
        if (reading.LastRaidAtUtc is { } lastRaid)
        {
            facts.Add(new(
                string.Create(culture, $"That raid's last line was {V2ShellText.Age(lastRaid, nowUtc, culture)} ({lastRaid:yyyy-MM-dd HH:mm} UTC)"),
                source));
        }

        facts.Add(new(
            reading.QueueTime is { } queue
                ? string.Create(culture, $"Queue time {queue.TotalSeconds:0.0} s, from the game's MatchingCompleted line")
                : "No queue time in this session — the game writes one only when matchmaking finishes",
            source));
        facts.Add(new(
            string.Create(culture, $"{reading.QuestEvents} quest notification(s) and {reading.FleaSales} flea sale(s)"),
            source));

        var understood = reading.RaidsSeen + reading.QuestEvents + reading.FleaSales;
        if (reading.LinesRead == 0)
        {
            return new(
                LogsId,
                "Logs",
                SelfTestOutcome.Fail,
                string.Create(culture, $"{reading.FileName} is {Bytes(reading.Bytes, culture)} and not one line came back."),
                facts,
                took);
        }

        return understood == 0 && reading.QueueTime is null
            ? Unknown(
                LogsId,
                "Logs",
                string.Create(culture, $"Read {reading.LinesRead:N0} lines and recognised nothing in them."),
                facts,
                took)
            : new(
                LogsId,
                "Logs",
                SelfTestOutcome.Pass,
                string.Create(culture, $"Understood {understood} event(s) from this session."),
                facts,
                took);
    }

    public static SelfTestCapability Screenshots(
        SelfTestScreenshot reading,
        TimeSpan took,
        CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(culture);
        if (reading.Problem is { Length: > 0 } problem)
        {
            return Unknown(ScreenshotsId, "Screenshots", problem, [], took);
        }

        if (reading.FileName is null)
        {
            return Unknown(
                ScreenshotsId,
                "Screenshots",
                string.Create(culture, $"No screenshot arrived in {reading.Waited.TotalSeconds:0} s. Press the game's screenshot key while this is running."),
                [new(
                    string.Create(culture, $"Watched {reading.Root} for {reading.Waited.TotalSeconds:0} s and nothing new appeared"),
                    "the screenshot folder, listed repeatedly")],
                took);
        }

        var noticed = reading.NoticedUtc ?? default;
        var source = ReadAt(noticed, "the screenshot's own file", culture);
        var facts = new List<SelfTestFact>(4)
        {
            new(string.Create(culture, $"{reading.FileName} appeared after {reading.Waited.TotalSeconds:0.0} s of watching"), source),
        };
        if (reading.WrittenUtc is { } written)
        {
            facts.Add(new(
                string.Create(culture, $"The game wrote it at {written:yyyy-MM-dd HH:mm:ss} UTC, taken from {reading.Clock}"),
                source));
        }

        if (reading.EndToEnd is { } endToEnd)
        {
            facts.Add(new(
                string.Create(culture, $"{endToEnd.TotalMilliseconds:N0} ms end to end, from the game writing the file to this position being parsed"),
                source));
        }

        if (!reading.Parsed)
        {
            facts.Add(new("No position came out of that name, so this screenshot would put nobody on the map", source));
            return new(
                ScreenshotsId,
                "Screenshots",
                SelfTestOutcome.Fail,
                "A screenshot arrived and no position could be read from its name.",
                facts,
                took);
        }

        facts.Add(new(
            string.Create(culture, $"Position {reading.X:0.0}, {reading.Y:0.0}, {reading.Z:0.0}, read from the name"),
            source));
        return new(
            ScreenshotsId,
            "Screenshots",
            SelfTestOutcome.Pass,
            string.Create(culture, $"A screenshot arrived and gave a position in {reading.EndToEnd?.TotalMilliseconds ?? 0:N0} ms."),
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
        var source = ReadAt(reading.ReadUtc, "the local database's own sync record", culture);
        if (reading.Problem is { Length: > 0 } problem)
        {
            return Unknown(GameDataId, "Game data", problem, [], took);
        }

        if (reading.Endpoints.Count == 0)
        {
            return Unknown(
                GameDataId,
                "Game data",
                "No endpoint has ever been recorded, so there was nothing to check.",
                [new($"No rows in the sync record for {reading.GameMode}/{reading.Language}", source)],
                took);
        }

        var facts = new List<SelfTestFact>(reading.Endpoints.Count);
        var broken = new List<string>();
        foreach (var endpoint in reading.Endpoints)
        {
            var age = endpoint.RefreshedUtc is { } at
                ? V2ShellText.Age(at, nowUtc, culture)
                : "never refreshed";
            if (endpoint.Error is { Length: > 0 } error)
            {
                broken.Add(endpoint.Name);
                facts.Add(new(
                    string.Create(culture, $"{endpoint.Name}: did not refresh — {error} (last good copy {age})"),
                    source));
                continue;
            }

            if (endpoint.Rows is 0 or null)
            {
                broken.Add(endpoint.Name);
                facts.Add(new(
                    string.Create(culture, $"{endpoint.Name}: {Bytes(endpoint.Bytes, culture)} cached, {age}, and no rows landed"),
                    source));
                continue;
            }

            facts.Add(new(
                string.Create(culture, $"{endpoint.Name}: {endpoint.Rows:N0} row(s), {Bytes(endpoint.Bytes, culture)}, {age}"),
                source));
        }

        return broken.Count > 0
            ? new(
                GameDataId,
                "Game data",
                SelfTestOutcome.Fail,
                string.Create(culture, $"{string.Join(", ", broken)} did not land."),
                facts,
                took)
            : new(
                GameDataId,
                "Game data",
                SelfTestOutcome.Pass,
                string.Create(culture, $"All {reading.Endpoints.Count} endpoints have rows."),
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
            return Unknown(DatabaseId, "Database", reading.Problem ?? "There is no database to read.", [], took);
        }

        var source = ReadAt(reading.ReadUtc, "the database itself", culture);
        var missing = reading.Expected.Except(reading.Applied, StringComparer.Ordinal).ToArray();
        var facts = new List<SelfTestFact>(reading.Tables.Count + 2)
        {
            new(string.Create(culture, $"{reading.Path} — {Bytes(reading.Bytes, culture)}"), source),
            new(
                missing.Length == 0
                    ? string.Create(culture, $"All {reading.Expected.Count} migrations applied, the newest being {reading.Applied.LastOrDefault() ?? "none"}")
                    : string.Create(culture, $"{missing.Length} migration(s) not applied: {string.Join(", ", missing)}"),
                "the schema_migrations table"),
        };
        facts.AddRange(reading.Tables.Select(table => new SelfTestFact(
            string.Create(culture, $"{table.Name}: {table.Rows:N0} row(s)"),
            source)));

        if (missing.Length > 0)
        {
            return new(
                DatabaseId,
                "Database",
                SelfTestOutcome.Fail,
                string.Create(culture, $"The schema is {missing.Length} migration(s) behind this build."),
                facts,
                took);
        }

        return reading.Bytes == 0
            ? new(DatabaseId, "Database", SelfTestOutcome.Fail, "The database file is empty.", facts, took)
            : new(
                DatabaseId,
                "Database",
                SelfTestOutcome.Pass,
                string.Create(culture, $"Schema current, {Bytes(reading.Bytes, culture)}, {reading.Tables.Count} table(s) counted."),
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
                "Relay",
                "No relay is configured, so there was nothing to reach.",
                [new("Sharing is off and no server address is saved", "the group settings file")],
                took);
        }

        var source = ReadAt(reading.ReadUtc, "one health read of the relay", culture);
        if (!reading.Reachable)
        {
            return new(
                RelayId,
                "Relay",
                SelfTestOutcome.Fail,
                string.Create(culture, $"{reading.Origin} did not answer."),
                [new(reading.Problem ?? "The health read did not come back", source)],
                took);
        }

        var facts = new List<SelfTestFact>(6)
        {
            new(
                string.Create(culture, $"{reading.Origin} answered in {reading.RoundTrip?.TotalMilliseconds ?? 0:N0} ms"),
                source),
            new(
                string.Create(culture, $"Running {reading.Version ?? "an unnamed build"}{(reading.Commit is null ? string.Empty : $" ({reading.Commit})")}, protocol {reading.Protocol?.ToString(culture) ?? "unknown"}"),
                source),
            new(
                string.Create(culture, $"{reading.Rooms ?? 0} room(s) and {reading.Members ?? 0} member(s) on the relay"),
                source),
            new(
                reading.Sharing
                    ? string.Create(culture, $"Sharing is on as {reading.MyName ?? "an unnamed player"}, with {reading.Others.Count} other(s) in the room")
                    : "Sharing is off, so nobody is being published to",
                "the group settings file and the last exchange"),
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
                string.Create(
                    culture,
                    $"Squadmate positions arrive in {latency.Median.TotalSeconds:0.00} s, {latency.Slowest95.TotalSeconds:0.00} s at the slow end, over {latency.SampleCount} of {latency.Delivered} delivered"),
                "timed on each delivery, from the sender's own age plus the relay's own wait")
            : new(
                freshest is { PositionAge: { } age }
                    ? string.Create(culture, $"Nothing has been delivered yet this session; the freshest marker is {age.TotalSeconds:0.0} s old ({freshest.Name})")
                    : reading.Others.Count == 0
                        ? "No squadmate is in the room, so position latency could not be measured"
                        : "No squadmate has published a position, so position latency could not be measured",
                "the last exchange with the relay"));

        if (reading.StaleSinceUtc is { } stale)
        {
            facts.Add(new(
                string.Create(culture, $"The group picture is stale; the last good exchange was {V2ShellText.Age(stale, nowUtc, culture)}"),
                source));
            return new(
                RelayId,
                "Relay",
                SelfTestOutcome.Fail,
                "The relay answers, but the group exchange has stopped working.",
                facts,
                took);
        }

        return new(
            RelayId,
            "Relay",
            SelfTestOutcome.Pass,
            string.Create(culture, $"Reachable in {reading.RoundTrip?.TotalMilliseconds ?? 0:N0} ms."),
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
                "Tablet",
                reading.Problem ?? "Pairing a tablet needs Windows and a relay with an HTTPS address.",
                [],
                took);
        }

        var source = ReadAt(reading.ReadUtc, "this desktop's own paired-device record", culture);
        var facts = new List<SelfTestFact>(reading.Devices.Count + 2);
        foreach (var device in reading.Devices)
        {
            facts.Add(new(
                string.Create(culture, $"{device.Name} ({device.Role}, {device.Status}) last seen {V2ShellText.Age(device.LastSeenUtc, nowUtc, culture)}"),
                source));
        }

        if (reading.Devices.Count == 0)
        {
            return Unknown(
                TabletId,
                "Tablet",
                "No device is paired, so there was nothing to publish to.",
                [new($"No paired devices at {reading.Origin ?? "this relay"}", source)],
                took);
        }

        facts.Add(new(
            reading.PublishedUtc is { } published
                ? string.Create(culture, $"The desktop published {reading.MapName ?? "a map"} with {reading.Objects:N0} object(s) {V2ShellText.Age(published, nowUtc, culture)}")
                : "The desktop has published no scene this session",
            "the tablet map publisher"));

        return reading.Publishing
            ? new(
                TabletId,
                "Tablet",
                SelfTestOutcome.Pass,
                string.Create(culture, $"{reading.Devices.Count} paired device(s), and the desktop is publishing a scene."),
                facts,
                took)
            : new(
                TabletId,
                "Tablet",
                SelfTestOutcome.Fail,
                string.Create(culture, $"{reading.Devices.Count} paired device(s), and the desktop has published nothing."),
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
        string.Create(culture, $"read from {what} at {at:HH:mm:ss} UTC");

    private static string Bytes(long bytes, CultureInfo culture) => bytes switch
    {
        < 0 => "unknown size",
        < 1024 => string.Create(culture, $"{bytes} B"),
        < 1024 * 1024 => string.Create(culture, $"{bytes / 1024.0:0.0} KB"),
        < 1024L * 1024 * 1024 => string.Create(culture, $"{bytes / (1024.0 * 1024):0.0} MB"),
        _ => string.Create(culture, $"{bytes / (1024.0 * 1024 * 1024):0.00} GB"),
    };
}
