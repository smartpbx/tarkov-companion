namespace TarkovCompanion.GroupServer;

/// <summary>
/// What build this relay is on, what build its signed release ring selects, and asking for the difference.
/// </summary>
/// <param name="Installed">
/// The relay archive digest the updater recorded after that build proved its identity, or null
/// before it has ever done so.
/// </param>
/// <param name="Published">
/// The relay archive digest named by the newest signed release decision the updater has
/// authenticated, or null before it has authenticated one.
/// </param>
/// <param name="Refused">
/// A build this machine installed, found unable to answer as itself, and rolled back. It is not
/// retried until the ring publishes a new signed decision, so a relay sitting behind with nothing
/// in the log to say why is explained by this field and by nothing else the operator can see.
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
/// The update half of the panel: which build is on the box against which build is selected.
/// </summary>
/// <remarks>
/// <para>
/// The relay updates itself every half hour and has done for a while. What it could not do is
/// say anything about that. A relay running an old build looks exactly like one running the
/// newest, and a build that was installed, failed its health check and was rolled back looks
/// like both — the updater records the refusal so it does not loop, and this surfaces it.
/// </para>
/// <para>
/// Every value comes from files the updater writes, and nothing here touches the network. The
/// panel used to fetch the public release's checksum itself, which gave it a second opinion
/// about what was published, one nobody had authenticated and that could disagree with the
/// updater. Only the updater holds the feed credential and the trust root, so only what it has
/// verified is reported.
/// </para>
/// <para>
/// Those files are read from the updater's status directory, never from this relay's own state
/// directory. The updater runs as root and this process does not; a stamp in a directory this
/// process can write is a stamp a compromised relay can write, so the updater stopped keeping
/// anything it decides from there, and this stopped reading anything it reports from there.
/// </para>
/// <para>
/// Asking for an update is a file, not a command. This process runs unprivileged and must not
/// be able to run one: it writes a marker in the state directory it already owns, and
/// <c>tarkov-group-update.path</c> turns that into the same update the timer runs. So the
/// button is the timer's own path, thirty minutes early, with no new privilege anywhere.
/// </para>
/// </remarks>
/// <param name="stateDirectory">This relay's own state directory, where the request marker is written.</param>
/// <param name="statusDirectory">The directory the updater publishes its status into, which this process only reads.</param>
public sealed class RelayUpdate(string? stateDirectory, string? statusDirectory)
{
    public bool IsAvailable => stateDirectory is { Length: > 0 };

    public RelayUpdateState Read()
    {
        if (!IsAvailable)
        {
            return new(null, null, null, false, false,
                "This relay has no state directory, so it does not know what build it is on.");
        }

        var requested = File.Exists(Path.Combine(stateDirectory!, "UPDATE_NOW"));
        if (!HasSeparateStatusDirectory())
        {
            return new(null, null, null, requested, true,
                "This relay has no updater status directory apart from its own, so it reports no build.");
        }

        var published = ReadStatus("PUBLISHED_SHA256");
        return new(
            ReadStatus("INSTALLED_SHA256"),
            published,
            ReadStatus("REFUSED_SHA256"),
            requested,
            true,
            published is null ? "The updater has not authenticated a signed release decision yet." : null);
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
    /// Whether status is configured somewhere other than inside the directory this process writes.
    /// </summary>
    /// <remarks>
    /// Pointing both at one directory would put the panel's claims back where the relay can forge
    /// them, which is the arrangement this class exists to have left.
    /// </remarks>
    private bool HasSeparateStatusDirectory()
    {
        if (statusDirectory is not { Length: > 0 })
        {
            return false;
        }

        var status = Path.TrimEndingDirectorySeparator(Path.GetFullPath(statusDirectory));
        var state = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stateDirectory!));
        return !string.Equals(status, state, StringComparison.Ordinal)
            && !status.StartsWith(state + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private string? ReadStatus(string fileName)
    {
        try
        {
            var path = Path.Combine(statusDirectory!, fileName);
            return File.Exists(path) ? File.ReadAllText(path).Trim() is { Length: 64 } sum ? sum : null : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
