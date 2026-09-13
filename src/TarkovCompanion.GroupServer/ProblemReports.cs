using System.Collections.Concurrent;
using System.Net.Http.Json;
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
/// The report is redacted before it leaves the client — no game logs, no group key, no
/// screenshots, no coordinates — and this adds nothing to it. It is a relay in the literal
/// sense.
/// </remarks>
public sealed class ProblemReports(TimeProvider timeProvider, IHttpClientFactory httpClientFactory)
{
    public const string HttpClientName = "github-issues";

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
    /// Files the report, and says where it went.
    /// </summary>
    /// <remarks>
    /// The reference is derived from the room and the moment rather than being random, so the
    /// player and the issue can be matched up later without the relay keeping a list of who
    /// reported what. It is a hash: the room is not recoverable from it.
    ///
    /// Without a token configured the report is still accepted and still stored, and the
    /// caller is told there is no issue. A relay that refused reports because nobody had set
    /// up GitHub would be a relay that loses the one thing it was asked to carry.
    /// </remarks>
    public async Task<ReportOutcome> FileAsync(string room, string body, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(room);
        ArgumentNullException.ThrowIfNull(body);

        var now = timeProvider.GetUtcNow();
        var reference = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{room}:{now:O}")))[..12].ToLowerInvariant();

        Store(reference, now, body);

        var token = Environment.GetEnvironmentVariable("TARKOV_GITHUB_TOKEN");
        var repository = Environment.GetEnvironmentVariable("TARKOV_GITHUB_REPOSITORY");
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(repository))
        {
            return new(reference, null, "Stored on the relay. No GitHub token is configured, so no issue was opened.");
        }

        try
        {
            using var client = httpClientFactory.CreateClient(HttpClientName);
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);
            using var response = await client
                .PostAsJsonAsync(
                    new Uri($"https://api.github.com/repos/{repository}/issues"),
                    new
                    {
                        title = $"Problem report {reference}",
                        body = $"Filed from the companion's Report a problem button.\n\n{body}",
                        labels = new[] { "problem-report" },
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new(reference, null, $"Stored on the relay. GitHub answered {(int)response.StatusCode}.");
            }

            var created = await response.Content
                .ReadFromJsonAsync<CreatedIssue>(cancellationToken)
                .ConfigureAwait(false);
            return new(reference, created?.HtmlUrl, "Filed.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // The report is already on disk, so the failure costs the link and not the report.
            return new(reference, null, $"Stored on the relay. GitHub could not be reached: {exception.Message}");
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
    private static void Store(string reference, DateTimeOffset now, string body)
    {
        try
        {
            var directory = Environment.GetEnvironmentVariable("TARKOV_GROUP_STATE")
                ?? Environment.GetEnvironmentVariable("STATE_DIRECTORY")?.Split(':')[0];
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            var reports = Path.Combine(directory, "reports");
            Directory.CreateDirectory(reports);
            File.WriteAllText(
                Path.Combine(reports, $"{now:yyyyMMdd-HHmmss}-{reference}.md"),
                body);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Losing the copy is not a reason to lose the issue as well.
        }
    }

    private sealed record CreatedIssue(
        [property: System.Text.Json.Serialization.JsonPropertyName("html_url")] string? HtmlUrl);
}

/// <summary>Where a report went, in the words the player is shown.</summary>
public sealed record ReportOutcome(string Reference, string? IssueUrl, string Detail);
