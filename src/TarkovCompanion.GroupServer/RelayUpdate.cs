namespace TarkovCompanion.GroupServer;

/// <summary>
/// What build this relay is on, what build is published, and asking for the difference.
/// </summary>
/// <param name="Installed">The checksum the updater recorded, or null before it has ever run.</param>
/// <param name="Published">The newest published checksum, or null if it could not be read.</param>
/// <param name="Refused">
/// A build this machine installed, found unable to answer, and rolled back. It is not retried
/// until a newer one is published, so a relay sitting behind with nothing in the log to say why
/// is explained by this field and by nothing else the operator can see.
/// </param>
/// <param name="Requested">Whether an update has been asked for and not yet started.</param>
/// <param name="Available">Whether this deployment can be asked at all.</param>
public sealed record RelayUpdateState(
    string? Installed,
    string? Published,
    string? Refused,
    bool Requested,
    bool Available,
    string? Detail);

/// <summary>
/// The update half of the panel: which build is on the box against which build is published.
/// </summary>
/// <remarks>
/// <para>
/// The relay updates itself every half hour and has done for a while. What it could not do is
/// say anything about that. A relay running an old build looks exactly like one running the
/// newest, and a build that was installed, failed its health check and was rolled back looks
/// like both — the updater records the refusal so it does not loop, and nothing surfaces it.
/// </para>
/// <para>
/// Asking for an update is a file, not a command. This process runs unprivileged and must not
/// be able to run one: it writes a marker in the state directory it already owns, and
/// <c>tarkov-group-update.path</c> turns that into the same update the timer runs. So the
/// button is the timer's own path, thirty minutes early, with no new privilege anywhere.
/// </para>
/// </remarks>
public sealed class RelayUpdate(string? stateDirectory, HttpClient? httpClient = null)
{
    /// <summary>The name this checker's own HTTP client is registered under.</summary>
    public const string HttpClientName = "relay-update";

    /// <summary>Where the published checksum is read from.</summary>
    /// <remarks>
    /// The same file, from the same release, that the updater itself verifies against. Reading a
    /// different source would let the panel say "up to date" about something the updater would
    /// then disagree with.
    /// </remarks>
    private const string SumsUrl =
        "https://github.com/smartpbx/tarkov-companion/releases/download/dev/GROUPSERVER-SHA256SUMS.txt";

    /// <summary>How long a published checksum is held before asking again.</summary>
    /// <remarks>
    /// The panel is refreshed by hand, and the release it reads changes a few times a day at
    /// most. Long enough that leaving the page open is not a request per second, short enough
    /// that an operator who has just merged something does not have to wait to see it.
    /// </remarks>
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(2);

    private readonly Lock _gate = new();
    private string? _published;
    private DateTimeOffset _fetchedUtc = DateTimeOffset.MinValue;

    public bool IsAvailable => stateDirectory is { Length: > 0 };

    public async Task<RelayUpdateState> ReadAsync(TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (!IsAvailable)
        {
            return new(null, null, null, false, false,
                "This relay has no state directory, so it does not know what build it is on.");
        }

        var published = await PublishedAsync(timeProvider, cancellationToken).ConfigureAwait(false);
        return new(
            Read("INSTALLED_SHA256"),
            published,
            Read("REFUSED_SHA256"),
            File.Exists(Path.Combine(stateDirectory!, "UPDATE_NOW")),
            true,
            published is null ? "The published checksum could not be read." : null);
    }

    /// <summary>
    /// Asks for an update, by writing the file the path unit watches.
    /// </summary>
    /// <returns>Whether the request was written.</returns>
    public bool Request()
    {
        if (!IsAvailable)
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(stateDirectory!);
            File.WriteAllText(Path.Combine(stateDirectory!, "UPDATE_NOW"), string.Empty);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The newest published checksum, cached.
    /// </summary>
    /// <remarks>
    /// Null when it cannot be read, which the panel says rather than treating as "up to date".
    /// A relay that cannot reach GitHub is a relay that is not updating, and that is worth
    /// knowing on the same page as the build it is stuck on.
    /// </remarks>
    private async Task<string?> PublishedAsync(TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        lock (_gate)
        {
            if (_published is not null && now - _fetchedUtc < CacheFor)
            {
                return _published;
            }
        }

        if (httpClient is null)
        {
            return null;
        }

        try
        {
            var body = await httpClient.GetStringAsync(SumsUrl, cancellationToken).ConfigureAwait(false);
            var first = body.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            var checksum = first?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (checksum is not { Length: 64 })
            {
                return null;
            }

            lock (_gate)
            {
                _published = checksum;
                _fetchedUtc = now;
            }

            return checksum;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    private string? Read(string fileName)
    {
        try
        {
            var path = Path.Combine(stateDirectory!, fileName);
            return File.Exists(path) ? File.ReadAllText(path).Trim() is { Length: 64 } sum ? sum : null : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
