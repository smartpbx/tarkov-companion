using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TarkovCompanion.GroupServer.Diagnostics;

namespace TarkovCompanion.GroupServer;

/// <summary>
/// Takes a diagnostic report from a player and turns it into an issue somebody will read.
/// </summary>
/// <remarks>
/// Two players in one evening appeared in their group's member list and never on its map, and
/// both times the report had to be assembled by somebody else asking questions across Discord.
/// The clipboard button fixed the assembling; this fixes the asking. The person with the
/// problem presses one thing and the report arrives where the work happens.
///
/// The relay is the only piece of this system that is already reachable from everybody's
/// machine and already trusted with a key, which is why it is here and not in the client: a
/// desktop application filing issues would need a token on every player's disk.
///
/// The ordinary desktop submits a closed, bounded support projection. This component still accepts
/// an arbitrary caller string and keeps it unchanged, so what it does instead is bound how much, how
/// long and how fast (<see cref="ProblemReportLimits"/>), and keep an honest state for each report
/// (docs/RELAY_ADMIN.md). Schema enforcement of the body and explicit desktop confirmation before
/// sending remain open under #281.
/// </remarks>
public sealed class ProblemReports
{
    /// <summary>The largest report that will be accepted.</summary>
    /// <remarks>
    /// The ordinary client sends a bounded list of operational facts, which is a few kilobytes.
    /// Sixty-four is generous for that and small enough that nobody can post a book.
    /// </remarks>
    public const int MaximumBytes = 64 * 1024;

    /// <summary>How many reports one room may file in an hour.</summary>
    /// <remarks>
    /// Three. A player diagnosing something presses the button once, maybe twice. A client
    /// stuck in a loop would otherwise open an issue every few seconds, which turns a useful
    /// signal into a reason to stop reading them.
    /// </remarks>
    public const int MaximumPerRoomPerHour = 3;

    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    /// <summary>How long a stray temporary or state file is left before it is taken for debris.</summary>
    private static readonly TimeSpan DebrisAge = TimeSpan.FromHours(1);

    /// <summary>The shape <see cref="Store"/> names a file, so a listed reference is always one
    /// <see cref="Read"/> can look up again.</summary>
    /// <remarks>
    /// #310: <see cref="List"/> used to hand back the whole basename - timestamp prefix and all -
    /// while <see cref="Read"/> only ever accepted the twelve-hex reference, so a report a player
    /// could see never had a URL that could read it. Matched here instead of trusted, because
    /// this directory is state the updater does not replace and a name that does not fit the
    /// shape this class writes is skipped rather than believed.
    /// </remarks>
    private static readonly Regex ReportFileName = new(
        @"^(?<stamp>\d{8}-\d{6})-(?<reference>[0-9a-f]{12})\.md$",
        RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions MetaJson = new(JsonSerializerDefaults.Web) { MaxDepth = 8 };

    private const int MaximumMetaBytes = 2048;

    private readonly TimeProvider _timeProvider;
    private readonly ProblemReportLimits _limits;
    private readonly string? _reportsDirectory;
    private readonly ConcurrentDictionary<string, List<DateTimeOffset>> _filed = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly List<DateTimeOffset> _acceptedRecently = [];
    private DateTimeOffset _lastSweepUtc;

    /// <param name="timeProvider">The clock.</param>
    /// <param name="limits">What may be held; the defaults when null.</param>
    /// <param name="reportsDirectory">Where reports are kept; the state directory's <c>reports</c> folder when null.</param>
    public ProblemReports(TimeProvider timeProvider, ProblemReportLimits? limits = null, string? reportsDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        _limits = limits ?? ProblemReportLimits.Default;
        _reportsDirectory = reportsDirectory;
    }

    /// <summary>Whether this room has filed too much recently.</summary>
    public bool IsRateLimited(string room)
    {
        var now = _timeProvider.GetUtcNow();
        var recent = _filed.GetOrAdd(room, _ => []);
        lock (recent)
        {
            recent.RemoveAll(at => now - at > Window);
            if (recent.Count >= MaximumPerRoomPerHour)
            {
                return true;
            }

            recent.Add(now);
            return false;
        }
    }

    /// <summary>
    /// Takes the report, keeps it, and hands back the reference it was filed under, or says why it
    /// was not kept.
    /// </summary>
    /// <remarks>
    /// The relay does not call GitHub. It has no token and should not have one: it is the
    /// internet-facing box in this system, and a long-lived credential with write access to
    /// the repository is exactly the thing not to keep on it.
    ///
    /// The hourly relay-watch workflow reads the references and opens the issues instead,
    /// using the token GitHub Actions already gives it for its own repository. That costs up
    /// to an hour and no secret at all.
    ///
    /// The reference is derived from the room and the moment rather than being random, so a
    /// player and an issue can be matched up without a separate index of who reported what.
    /// It is not an anonymity control: the file name keeps the arrival second, the relay holds
    /// the candidate room hashes, and the body itself can name the reporter.
    ///
    /// Admission and storage happen under one lock, so a burst of requests cannot each see room for
    /// one more and together overfill what is held.
    /// </remarks>
    public ReportOutcome Accept(string room, string body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(room);
        ArgumentNullException.ThrowIfNull(body);

        var now = _timeProvider.GetUtcNow();
        var reference = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{room}:{now:O}")))[..12].ToLowerInvariant();
        var bytes = Encoding.UTF8.GetByteCount(body);

        lock (_gate)
        {
            var admission = Admit(room, bytes, now);
            if (admission != ReportAdmission.Accepted)
            {
                return new ReportOutcome(string.Empty, Refusal(admission)) { Admission = admission };
            }

            if (Store(room, reference, now, body))
            {
                _acceptedRecently.Add(now);
                return new(reference, "Sent. It will be picked up within the hour.");
            }
        }

        return new(reference, "Sent, but the relay could not keep a copy. Use Copy diagnostics as well.");
    }

    /// <summary>
    /// The reports still waiting to become an issue, newest first, without their contents.
    /// </summary>
    /// <remarks>
    /// References and sizes only. The bodies stay here: an issue naming a reference is enough
    /// for somebody to come and read one, and the repository is public, so putting a player's
    /// machine details in the issue itself would publish them.
    ///
    /// This is the filing queue, so it leaves out what is already processed and what has failed as many
    /// times as it will be tried; <see cref="Ledger"/> has all of them. The shape is deliberately
    /// unchanged: relay-watch validates it against a closed schema and refuses anything else.
    /// </remarks>
    public IReadOnlyList<StoredReport> List()
    {
        lock (_gate)
        {
            return
            [
                .. Held()
                    .Where(report => report.State == ReportState.Received ||
                        (report.State == ReportState.Failed && report.Attempts < _limits.MaximumFilingAttempts))
                    .Take(50)
                    .Select(report => new StoredReport(report.Reference, report.Bytes, report.ReceivedUtc)),
            ];
        }
    }

    /// <summary>Everything held, with its state, and how close the relay is to its limits.</summary>
    public ReportLedger Ledger()
    {
        lock (_gate)
        {
            var held = Held();
            var directory = ReportsDirectory();
            return new ReportLedger(
                held.Count,
                held.Sum(report => report.Bytes),
                held.Count(report => report.State == ReportState.Received),
                held.Count(report => report.State == ReportState.Processed),
                held.Count(report => report.State == ReportState.Failed),
                _limits.MaximumHeld,
                _limits.MaximumHeldBytes,
                (int)_limits.TimeToLive.TotalDays,
                directory is null ? null : RelayVolume.FreeMegabytes(directory),
                _limits.MinimumFreeMegabytes,
                [.. held.Select(report => new LedgerEntry(
                    report.Reference, report.Bytes, report.ReceivedUtc, report.State.ToString().ToLowerInvariant(), report.Attempts))]);
        }
    }

    /// <summary>One report's text, for whoever holds the admin key.</summary>
    /// <remarks>
    /// The reference is used as a filename fragment, so it is checked against the shape this
    /// class produces rather than trusted: a reference is twelve lowercase hex characters and
    /// nothing else, which cannot contain a path separator or a dot.
    /// </remarks>
    public string? Read(string reference)
    {
        if (!IsReference(reference))
        {
            return null;
        }

        try
        {
            if (ReportsDirectory() is not { } reports || !Directory.Exists(reports))
            {
                return null;
            }

            var file = Directory.EnumerateFiles(reports, $"*-{reference}.md").FirstOrDefault();
            return file is null ? null : File.ReadAllText(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>An issue names this report. Repeating it changes nothing.</summary>
    public ReportTransition MarkProcessed(string reference) => Transition(reference, ReportState.Processed);

    /// <summary>Filing this report failed once more. A processed report is never downgraded.</summary>
    public ReportTransition MarkFailed(string reference) => Transition(reference, ReportState.Failed);

    /// <summary>
    /// Removes a report and its state. True when there was one to remove; asking again is not an error.
    /// </summary>
    public bool Delete(string reference)
    {
        if (!IsReference(reference))
        {
            return false;
        }

        lock (_gate)
        {
            return Held().FirstOrDefault(report => report.Reference == reference) is { } report && Remove(report);
        }
    }

    /// <summary>
    /// Forgets what has outlived its reason to be kept: reports past their time to live, and the debris
    /// a crash between two writes can leave. Cheap enough to call every minute; it does its work hourly.
    /// </summary>
    /// <returns>How many reports were removed.</returns>
    public int Sweep()
    {
        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            if (_lastSweepUtc != default && now - _lastSweepUtc < Window)
            {
                return 0;
            }

            _lastSweepUtc = now;
            if (ReportsDirectory() is not { } directory || !Directory.Exists(directory))
            {
                return 0;
            }

            var removed = Held().Where(report => now - report.ReceivedUtc > _limits.TimeToLive).Count(Remove);
            RemoveDebris(directory, now);
            return removed;
        }
    }

    private ReportAdmission Admit(string room, int bytes, DateTimeOffset now)
    {
        // With nowhere to keep a report there is nothing to fill: the caller is told it was not kept.
        if (ReportsDirectory() is not { } directory)
        {
            return ReportAdmission.Accepted;
        }

        _acceptedRecently.RemoveAll(at => now - at > Window);
        if (_acceptedRecently.Count >= _limits.MaximumPerHour)
        {
            return ReportAdmission.TooManyRecently;
        }

        var held = Held();
        if (held.Count >= _limits.MaximumHeld || held.Sum(report => report.Bytes) + bytes > _limits.MaximumHeldBytes)
        {
            return ReportAdmission.RelayFull;
        }

        if (held.Count(report => string.Equals(report.Room, room, StringComparison.Ordinal)) >= _limits.MaximumHeldPerRoom)
        {
            return ReportAdmission.RoomFull;
        }

        // A full disk should stop reports, not the relay: the state directory also holds the room
        // registry and the device registry, which must always be able to write.
        return RelayVolume.FreeMegabytes(directory) < _limits.MinimumFreeMegabytes + (bytes / (1024 * 1024)) + 1
            ? ReportAdmission.DiskPressure
            : ReportAdmission.Accepted;
    }

    private ReportTransition Transition(string reference, ReportState target)
    {
        if (!IsReference(reference))
        {
            return ReportTransition.NotFound;
        }

        lock (_gate)
        {
            if (Held().FirstOrDefault(report => report.Reference == reference) is not { } report)
            {
                return ReportTransition.NotFound;
            }

            if (target == ReportState.Processed)
            {
                if (report.State == ReportState.Processed)
                {
                    return ReportTransition.Unchanged;
                }

                return WriteMeta(report, ReportState.Processed, report.Attempts)
                    ? ReportTransition.Applied
                    : ReportTransition.Refused;
            }

            if (report.State == ReportState.Processed)
            {
                return ReportTransition.Refused;
            }

            return WriteMeta(report, ReportState.Failed, report.Attempts + 1)
                ? ReportTransition.Applied
                : ReportTransition.Refused;
        }
    }

    /// <summary>
    /// Keeps the report even when the issue cannot be opened.
    /// </summary>
    /// <remarks>
    /// Beside the marks, in the state directory the updater does not replace. A report that
    /// only existed as a GitHub call would be lost exactly when GitHub is the thing that is
    /// broken.
    ///
    /// Each file is written whole under a temporary name and renamed into place, the state first and
    /// the body last: the body is what makes a report exist, so a crash between the two leaves state
    /// with no report (debris, swept) and never a report the listing would count half written.
    /// </remarks>
    private bool Store(string room, string reference, DateTimeOffset now, string body)
    {
        try
        {
            if (ReportsDirectory() is not { } reports)
            {
                return false;
            }

            Directory.CreateDirectory(reports);
            var stem = Path.Combine(reports, $"{now:yyyyMMdd-HHmmss}-{reference}");
            WriteWhole(stem + ".state", JsonSerializer.Serialize(new ReportMeta(room, "received", 0, now), MetaJson));
            WriteWhole(stem + ".md", body);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool WriteMeta(HeldReport report, ReportState state, int attempts)
    {
        try
        {
            var meta = new ReportMeta(report.Room, state.ToString().ToLowerInvariant(), attempts, _timeProvider.GetUtcNow());
            WriteWhole(StatePath(report), JsonSerializer.Serialize(meta, MetaJson));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void WriteWhole(string path, string text)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, text);
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Every well-formed report, newest first. Anything that does not fit the shape written here is skipped.</summary>
    private List<HeldReport> Held()
    {
        var directory = ReportsDirectory();
        if (directory is null || !Directory.Exists(directory))
        {
            return [];
        }

        try
        {
            return
            [
                .. new DirectoryInfo(directory)
                    .EnumerateFiles("*.md")
                    .OrderByDescending(file => file.Name, StringComparer.Ordinal)
                    .Select(file => (file, match: ReportFileName.Match(file.Name)))
                    .Where(entry => entry.match.Success)
                    .Select(entry =>
                    {
                        var meta = ReadMeta(Path.ChangeExtension(entry.file.FullName, ".state"));
                        return new HeldReport(
                            entry.match.Groups["reference"].Value,
                            entry.file.FullName,
                            entry.file.Length,
                            ReceivedUtc(entry.match.Groups["stamp"].Value, entry.file),
                            meta.ParsedState,
                            meta.Attempts,
                            meta.Room);
                    }),
            ];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // The moment it was accepted, from the name this class wrote it under; the file's own time only
    // when the name cannot be read, since a copy or a restore changes that and not the name.
    private static DateTimeOffset ReceivedUtc(string stamp, FileInfo file) =>
        DateTimeOffset.TryParseExact(
            stamp,
            "yyyyMMdd-HHmmss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : new DateTimeOffset(file.CreationTimeUtc, TimeSpan.Zero);

    /// <summary>
    /// A report's state, or "received" when the state file is missing or cannot be trusted.
    /// </summary>
    /// <remarks>
    /// Fails toward keeping and re-examining, never toward losing a report: the worst outcome of a
    /// damaged state file is a second look, which the workflow's duplicate check absorbs.
    /// </remarks>
    private static ReportMeta ReadMeta(string path)
    {
        var fallback = new ReportMeta(null, "received", 0, default);
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.LinkTarget is not null || file.Length > MaximumMetaBytes)
            {
                return fallback;
            }

            var meta = JsonSerializer.Deserialize<ReportMeta>(File.ReadAllText(path), MetaJson);
            return meta is null || meta.Attempts < 0 || meta.Attempts > 1_000 ? fallback : meta;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return fallback;
        }
    }

    private bool Remove(HeldReport report)
    {
        try
        {
            File.Delete(report.Path);
            File.Delete(StatePath(report));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string StatePath(HeldReport report) => Path.ChangeExtension(report.Path, ".state");

    private static void RemoveDebris(string directory, DateTimeOffset now)
    {
        try
        {
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles())
            {
                var age = now - new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
                var stray = file.Name.EndsWith(".tmp", StringComparison.Ordinal) ||
                    (file.Name.EndsWith(".state", StringComparison.Ordinal) &&
                     !File.Exists(Path.ChangeExtension(file.FullName, ".md")));
                if (stray && age > DebrisAge)
                {
                    file.Delete();
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Debris is only debris; the next sweep tries again.
        }
    }

    private static bool IsReference(string? reference) =>
        reference is { Length: 12 } && reference.All(character => char.IsAsciiDigit(character) || (character >= 'a' && character <= 'f'));

    private static string Refusal(ReportAdmission admission) => admission switch
    {
        ReportAdmission.RelayFull => "The relay is holding as many reports as it will. Try again later, and use Copy diagnostics.",
        ReportAdmission.RoomFull => "This group already has several reports waiting. Try again later, and use Copy diagnostics.",
        ReportAdmission.TooManyRecently => "The relay has taken a lot of reports in the last hour. Try again later, and use Copy diagnostics.",
        _ => "The relay is short of space and is not taking reports right now. Use Copy diagnostics.",
    };

    /// <summary>Beside the marks, in the directory the updater does not replace.</summary>
    private string? ReportsDirectory()
    {
        if (_reportsDirectory is not null)
        {
            return _reportsDirectory;
        }

        var directory = Environment.GetEnvironmentVariable("TARKOV_GROUP_STATE")
            ?? Environment.GetEnvironmentVariable("STATE_DIRECTORY")?.Split(':')[0];
        return string.IsNullOrWhiteSpace(directory) ? null : Path.Combine(directory, "reports");
    }

    private sealed record ReportMeta(string? Room, string State, int Attempts, DateTimeOffset UpdatedUtc)
    {
        [JsonIgnore]
        public ReportState ParsedState => State switch
        {
            "processed" => ReportState.Processed,
            "failed" => ReportState.Failed,
            _ => ReportState.Received,
        };
    }

    private sealed record HeldReport(
        string Reference,
        string Path,
        long Bytes,
        DateTimeOffset ReceivedUtc,
        ReportState State,
        int Attempts,
        string? Room);
}

public enum ReportState
{
    Received = 1,
    Processed,
    Failed,
}

public enum ReportAdmission
{
    Accepted = 1,
    RelayFull,
    RoomFull,
    TooManyRecently,
    DiskPressure,
}

public enum ReportTransition
{
    Applied = 1,
    Unchanged,
    NotFound,
    Refused,
}

/// <summary>Where a report went, in the words the player is shown.</summary>
public sealed record ReportOutcome(string Reference, string Detail)
{
    /// <summary>Whether the report was kept; not sent to the player, who is told by the status and the detail.</summary>
    [JsonIgnore]
    public ReportAdmission Admission { get; init; } = ReportAdmission.Accepted;
}

/// <summary>One report the relay is holding, named and sized but not read out.</summary>
public sealed record StoredReport(string Reference, long Bytes, DateTimeOffset ReceivedUtc);

/// <summary>What the operator sees of every report held: state and size, never a body.</summary>
public sealed record LedgerEntry(string Reference, long Bytes, DateTimeOffset ReceivedUtc, string State, int Attempts);

public sealed record ReportLedger(
    int Held,
    long Bytes,
    int Received,
    int Processed,
    int Failed,
    int MaximumHeld,
    long MaximumBytes,
    int TimeToLiveDays,
    int? FreeDiskMegabytes,
    int MinimumFreeDiskMegabytes,
    IReadOnlyList<LedgerEntry> Reports);
