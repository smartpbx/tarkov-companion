using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

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
/// The client intentionally excludes game logs, the group key, and screenshot pixels, but its
/// current field-by-field redaction is not a whole-payload guarantee. This component persists the
/// submitted body unchanged; #281 and #310 own the client preview/filter and relay allowlist.
/// </remarks>
public sealed class ProblemReports(TimeProvider timeProvider)
{
    /// <summary>The largest report that will be accepted.</summary>
    /// <remarks>
    /// The client sends a log tail and a handful of facts, which is a few kilobytes. Sixty-four
    /// is generous for that and small enough that nobody can post a book.
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

    private readonly ConcurrentDictionary<string, List<DateTimeOffset>> _filed = new(StringComparer.Ordinal);

    /// <summary>Whether this room has filed too much recently.</summary>
    public bool IsRateLimited(string room)
    {
        var now = timeProvider.GetUtcNow();
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
    /// Takes the report, keeps it, and hands back the reference it was filed under.
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
    /// player and an issue can be matched up without the relay keeping a list of who reported
    /// what. It is a hash: the room is not recoverable from it.
    /// </remarks>
    public ReportOutcome Accept(string room, string body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(room);
        ArgumentNullException.ThrowIfNull(body);

        var now = timeProvider.GetUtcNow();
        var reference = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{room}:{now:O}")))[..12].ToLowerInvariant();

        return Store(reference, now, body)
            ? new(reference, "Sent. It will be picked up within the hour.")
            : new(reference, "Sent, but the relay could not keep a copy. Use Copy diagnostics as well.");
    }

    /// <summary>
    /// The reports held, newest first, without their contents.
    /// </summary>
    /// <remarks>
    /// References and sizes only. The bodies stay here: an issue naming a reference is enough
    /// for somebody to come and read one, and the repository is public, so putting a player's
    /// machine details in the issue itself would publish them.
    /// </remarks>
    public IReadOnlyList<StoredReport> List()
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
                    .Take(50)
                    .Select(file => new StoredReport(
                        Path.GetFileNameWithoutExtension(file.Name),
                        file.Length,
                        new DateTimeOffset(file.CreationTimeUtc, TimeSpan.Zero))),
            ];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
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
        if (reference is null ||
            reference.Length != 12 ||
            !reference.All(character => char.IsAsciiDigit(character) || (character >= 'a' && character <= 'f')))
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

    /// <summary>
    /// Keeps the report even when the issue cannot be opened.
    /// </summary>
    /// <remarks>
    /// Beside the marks, in the state directory the updater does not replace. A report that
    /// only existed as a GitHub call would be lost exactly when GitHub is the thing that is
    /// broken.
    /// </remarks>
    private static bool Store(string reference, DateTimeOffset now, string body)
    {
        try
        {
            if (ReportsDirectory() is not { } reports)
            {
                return false;
            }

            Directory.CreateDirectory(reports);
            File.WriteAllText(Path.Combine(reports, $"{now:yyyyMMdd-HHmmss}-{reference}.md"), body);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Beside the marks, in the directory the updater does not replace.</summary>
    private static string? ReportsDirectory()
    {
        var directory = Environment.GetEnvironmentVariable("TARKOV_GROUP_STATE")
            ?? Environment.GetEnvironmentVariable("STATE_DIRECTORY")?.Split(':')[0];
        return string.IsNullOrWhiteSpace(directory) ? null : Path.Combine(directory, "reports");
    }

}

/// <summary>Where a report went, in the words the player is shown.</summary>
public sealed record ReportOutcome(string Reference, string Detail);

/// <summary>One report the relay is holding, named and sized but not read out.</summary>
public sealed record StoredReport(string Reference, long Bytes, DateTimeOffset ReceivedUtc);
