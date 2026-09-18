using System.Text.RegularExpressions;

namespace TarkovCompanion.GroupServer;

/// <summary>
/// The desktop's rough update channel, served as plain files from a folder.
/// </summary>
/// <remarks>
/// The relay hosts it because the relay is the one public, TLS-fronted host this project already
/// runs, and the alternative was a private GitHub feed that would need a token compiled into
/// every client.
///
/// It is kept apart from everything else this server does, on purpose:
///
/// - It reads files and nothing else. There is no upload route: builds arrive by an operator
///   copying them onto the box, so nothing reachable from the network can change what is served.
/// - It takes no key and keeps no state. It never touches rooms, marks, pairing or the device
///   registry, and the room gate does not apply to it.
/// - It costs nothing when idle: no timer, no cache, no watcher, nothing read at startup. A
///   request opens one file and the kernel sends it. The folder need not exist, in which case
///   every request is a 404.
///
/// A name is a channel and a file, each matched against a short pattern, so a request cannot
/// name anything outside the folder. This says nothing about who published a build; see
/// docs/RELEASES.md for what the channel does and does not prove.
/// </remarks>
public static partial class UpdateFeedFiles
{
    /// <summary>Overrides where the channel folders are.</summary>
    public const string RootVariable = "TARKOV_UPDATE_FEED_ROOT";

    /// <summary>Outside the install tree, so a relay update does not remove it, and outside the
    /// relay's own state directory, so the relay's dynamic user has no write access to it.</summary>
    public const string DefaultRoot = "/srv/tarkov-updates";

    public static string Root() =>
        Environment.GetEnvironmentVariable(RootVariable) is { Length: > 0 } configured
            ? configured
            : DefaultRoot;

    /// <summary>
    /// The file a request names, or null when the names are not ones this serves.
    /// </summary>
    public static string? Resolve(string root, string? channel, string? file)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (channel is null || file is null
            || !ChannelName().IsMatch(channel)
            || !FileName().IsMatch(file)
            || file.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        var folder = Path.GetFullPath(Path.Combine(root, channel));
        var path = Path.GetFullPath(Path.Combine(folder, file));
        // The patterns already exclude separators. This is the statement of what they are for.
        return path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? path : null;
    }

    /// <summary>
    /// Whether a file may be cached by the proxy in front of this relay.
    /// </summary>
    /// <remarks>
    /// Only a package, because only a package is named after its version and so never changes
    /// under its name. The feed changes with every build, and a client holding yesterday's feed
    /// is a client that is quietly not updating. The installer keeps one name across builds, and
    /// the proxy caches that extension by default, so it is marked the same way.
    /// </remarks>
    public static bool IsCacheable(string file) =>
        file.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase);

    public static string ContentType(string file) =>
        file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "application/json"
        : file.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? "text/plain; charset=utf-8"
        : "application/octet-stream";

    /// <summary>Maps <c>GET /updates/{channel}/{file}</c>.</summary>
    public static void MapUpdateFeed(this IEndpointRouteBuilder app, string root)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapMethods("/updates/{channel}/{file}", ["GET", "HEAD"], (string channel, string file, HttpContext context) =>
        {
            var path = Resolve(root, channel, file);
            if (path is null || !File.Exists(path))
            {
                return Results.NotFound();
            }

            context.Response.Headers.CacheControl = IsCacheable(file) ? "public, max-age=3600" : "no-cache";
            // Ranges, so a download that drops at 80 MB can resume instead of starting again.
            return Results.File(path, ContentType(file), enableRangeProcessing: true);
        });
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,31}$")]
    private static partial Regex ChannelName();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,199}$")]
    private static partial Regex FileName();
}
