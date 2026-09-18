namespace TarkovCompanion.App.Services.Updates;

/// <summary>Where builds come from, and where the installer that follows them is.</summary>
/// <param name="Name">What the settings page calls it.</param>
/// <param name="Feed">The folder the feed and its packages are served from.</param>
/// <param name="Installer">The installer a build run from a folder is pointed at.</param>
public sealed record UpdateChannel(string Name, Uri Feed, Uri Installer)
{
    /// <summary>Overrides the feed location: an https URL, or a folder holding a feed.</summary>
    public const string FeedOverrideVariable = "TARKOV_UPDATE_FEED";

    private const string InstallerFileName = "TarkovCompanionDesktop-win-Setup.exe";

    /// <summary>
    /// The rough test builds, served as static files by the relay.
    /// </summary>
    /// <remarks>
    /// On the relay rather than in a private GitHub feed because a private feed needs a token,
    /// and a token compiled into a client is a token published. This needs none: it is public,
    /// read-only, and its integrity story is TLS plus the SHA256 in the feed and no more than
    /// that. The signed ring (docs/RELEASES.md, #280) replaces it.
    /// </remarks>
    public static UpdateChannel Rough { get; } = At("Rough test builds", new Uri("https://tarkov.mannerow.net/updates/rough/"));

    /// <summary>The rough channel, unless the environment points somewhere else.</summary>
    public static UpdateChannel FromEnvironment()
    {
        var configured = Environment.GetEnvironmentVariable(FeedOverrideVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return Rough;
        }

        if (Uri.TryCreate(configured, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
        {
            return At($"Custom · {uri.Host}", uri);
        }

        return Directory.Exists(configured)
            ? At("Custom · local folder", new Uri(Path.GetFullPath(configured) + Path.DirectorySeparatorChar))
            : Rough;
    }

    /// <summary>The transport that reads this channel.</summary>
    public IUpdateFeedTransport OpenTransport(HttpClient client) => Feed.IsFile
        ? new DirectoryUpdateFeedTransport(Feed.LocalPath)
        : new HttpUpdateFeedTransport(client, Feed);

    private static UpdateChannel At(string name, Uri feed)
    {
        var folder = feed.AbsoluteUri.EndsWith('/') ? feed : new Uri(feed.AbsoluteUri + "/");
        return new UpdateChannel(name, folder, new Uri(folder, InstallerFileName));
    }
}
