using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TarkovCompanion.GroupServer;

/// <summary>
/// Serves the game-data catalog once for the whole group.
/// </summary>
/// <remarks>
/// <para>
/// Every client syncs several megabytes from json.tarkov.dev into its own database, on its own
/// schedule, over its own connection. A group of five does that five times for five identical
/// answers. The server is already running, already reachable by all of them, and can hold one
/// copy.
/// </para>
/// <para>
/// What it serves is the upstream payload byte for byte, with a strong tag that is the SHA-256
/// of those bytes. That makes a snapshot content-addressed: two clients holding the same tag
/// are holding the same catalog, and a client that already has it is answered with a 304 and
/// no body at all. It is deliberately not a reshaped or normalised catalog yet; see the README
/// for why that is a separate step and what it would take.
/// </para>
/// <para>
/// The one rule that matters: <b>this must never become a dependency.</b> A client that cannot
/// reach the server goes straight to upstream, which is what it did before this existed, and a
/// server that cannot reach upstream says so rather than serving something older than the
/// client already has.
/// </para>
/// </remarks>
public sealed class CatalogMirror(HttpClient httpClient, TimeProvider timeProvider)
{
    /// <summary>How long a held snapshot is served before upstream is asked again.</summary>
    /// <remarks>
    /// The client's own freshness window for static data is nine hours. An hour here means the
    /// group sees a change within an hour of it landing while still collapsing a session's
    /// worth of requests into one.
    /// </remarks>
    private static readonly TimeSpan FreshFor = TimeSpan.FromHours(1);

    /// <summary>The paths that may be asked for, which is the whole of the upstream surface we use.</summary>
    /// <remarks>
    /// An allowlist rather than a pattern. An open proxy on a public address is somebody else's
    /// bandwidth bill, and "it only forwards to one host" is a sentence that stops being true
    /// the first time the path is built from user input.
    /// </remarks>
    private static readonly string[] Endpoints =
    [
        "items",
        "maps",
        "tasks",
        "hideout",
        "traders",
        "barters",
        "crafts",
        "ammo",
        "achievements",
        "status",
    ];

    private static readonly string[] Modes = ["regular", "pve"];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Snapshot> _held = new(StringComparer.Ordinal);

    /// <summary>One catalog payload as it was fetched, and what it hashes to.</summary>
    /// <param name="Body">The upstream bytes, unchanged.</param>
    /// <param name="ETag">The SHA-256 of those bytes, quoted, as a strong entity tag.</param>
    /// <param name="FetchedUtc">When it was fetched, which is what freshness is measured from.</param>
    public sealed record Snapshot(byte[] Body, string ETag, DateTimeOffset FetchedUtc);

    /// <summary>Whether this is a path the mirror will fetch at all.</summary>
    public static bool IsAllowed(string? mode, string? endpoint) =>
        mode is not null &&
        endpoint is not null &&
        Modes.Contains(mode, StringComparer.Ordinal) &&
        Endpoints.Contains(endpoint, StringComparer.Ordinal);

    /// <summary>What the mirror is holding, for a client deciding whether to ask.</summary>
    public IReadOnlyList<CatalogEntry> Index()
    {
        lock (_held)
        {
            return
            [
                .. _held
                    .Select(entry => new CatalogEntry(entry.Key, entry.Value.ETag, entry.Value.FetchedUtc, entry.Value.Body.Length))
                    .OrderBy(entry => entry.Path, StringComparer.Ordinal),
            ];
        }
    }

    /// <summary>
    /// The snapshot for one path, fetching it if what is held is old or missing.
    /// </summary>
    /// <remarks>
    /// A fetch that fails while something is held serves what is held and says nothing about
    /// the failure: the client's own fallback is the upstream it would have used anyway, and
    /// refusing here would send it there for an answer we already have.
    ///
    /// A fetch that fails with nothing held returns null, and the endpoint turns that into a
    /// 503 so the client falls back rather than caching an error.
    /// </remarks>
    public async Task<Snapshot?> GetAsync(string mode, string endpoint, CancellationToken cancellationToken)
    {
        var path = mode + "/" + endpoint;
        var now = timeProvider.GetUtcNow();
        lock (_held)
        {
            if (_held.TryGetValue(path, out var fresh) && now - fresh.FetchedUtc <= FreshFor)
            {
                return fresh;
            }
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Checked again inside the gate. Five clients starting together is the ordinary
            // case, and without this they would be five upstream requests for one answer,
            // which is the thing this class exists to stop.
            lock (_held)
            {
                if (_held.TryGetValue(path, out var fresh) && timeProvider.GetUtcNow() - fresh.FetchedUtc <= FreshFor)
                {
                    return fresh;
                }
            }

            var body = await httpClient
                .GetByteArrayAsync(new Uri("https://json.tarkov.dev/" + path), cancellationToken)
                .ConfigureAwait(false);
            // Checked before it is held. A truncated or error body served with a strong tag is
            // worse than no mirror at all, because every client would cache it under a tag that
            // says it is the real thing.
            if (!LooksLikeCatalog(body))
            {
                return Held(path);
            }

            var snapshot = new Snapshot(body, Tag(body), timeProvider.GetUtcNow());
            lock (_held)
            {
                _held[path] = snapshot;
            }

            return snapshot;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Held(path);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Snapshot? Held(string path)
    {
        lock (_held)
        {
            return _held.GetValueOrDefault(path);
        }
    }

    /// <summary>Whether this is the shape upstream returns, rather than an error page.</summary>
    private static bool LooksLikeCatalog(byte[] body)
    {
        if (body.Length < 32)
        {
            return false;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);
            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                document.RootElement.TryGetProperty("data", out _);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static string Tag(byte[] body)
    {
        var hash = SHA256.HashData(body);
        var builder = new StringBuilder(hash.Length * 2 + 2);
        builder.Append('"');
        foreach (var value in hash)
        {
            builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        }

        builder.Append('"');
        return builder.ToString();
    }
}

/// <summary>One path the mirror is holding, for a client deciding whether to ask for it.</summary>
/// <param name="Path">The upstream path, "regular/items".</param>
/// <param name="ETag">The SHA-256 of the bytes, which is what makes a snapshot addressable.</param>
/// <param name="FetchedUtc">When the server last read it from upstream.</param>
/// <param name="Bytes">How big it is, so a client on a phone can decide.</param>
public sealed record CatalogEntry(string Path, string ETag, DateTimeOffset FetchedUtc, int Bytes);
