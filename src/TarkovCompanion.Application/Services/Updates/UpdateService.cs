using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TarkovCompanion.Application.Services.Updates;

/// <summary>What a build says about itself, read from the file packaging leaves beside it.</summary>
/// <param name="Version">The product version.</param>
/// <param name="Commit">The commit the build was made from, or null when unknown.</param>
/// <param name="BuiltUtc">When it was built, or null when unknown.</param>
public sealed record BuildIdentity(string Version, string? Commit, DateTimeOffset? BuiltUtc)
{
    /// <summary>What a build with no stamp reports, which is what running from source looks like.</summary>
    public static BuildIdentity Unknown { get; } = new("unknown", null, null);

    public string ShortCommit => Commit is { Length: >= 7 } commit ? commit[..7] : "unknown";
}

/// <summary>The build being offered, as the published manifest describes it.</summary>
public sealed record PublishedBuild(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("commit")] string Commit,
    [property: JsonPropertyName("builtUtc")] DateTimeOffset BuiltUtc,
    [property: JsonPropertyName("asset")] string Asset,
    [property: JsonPropertyName("sha256")] string Sha256);

/// <summary>One file attached to the release, as the API lists it.</summary>
internal sealed record ReleaseAsset(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string Name);

internal sealed record Release(
    [property: JsonPropertyName("assets")] IReadOnlyList<ReleaseAsset> Assets);

public enum UpdateAvailability
{
    UpToDate,
    Available,
    Unknown,
}

/// <summary>The answer to "is there a newer build", in a form the interface can render.</summary>
/// <param name="Availability">Whether an update was found, none was needed, or nothing could be checked.</param>
/// <param name="Installed">What is running now.</param>
/// <param name="Published">What is being offered, when there is one.</param>
/// <param name="Detail">A sentence saying what happened, including why a check could not be made.</param>
public sealed record UpdateCheck(
    UpdateAvailability Availability,
    BuildIdentity Installed,
    PublishedBuild? Published,
    string Detail);

/// <summary>
/// Finds out whether a newer build has been published, and fetches it.
/// </summary>
/// <remarks>
/// Every build tonight reached the machine it was for by a person downloading an artifact,
/// checking a hash by hand and swapping a folder. That worked because somebody was awake to do
/// it; it is not how the application should be updated.
///
/// The source is one rolling pre-release that the Windows verification job replaces on every
/// build that passes it. Only verified builds are ever offered, which is the property that
/// makes automatic updating safe here: nothing that failed a check can be downloaded.
///
/// The repository is private, so reading the release needs a credential. Two are accepted, in
/// order: one configured in the application, and failing that whatever the GitHub CLI is
/// already signed in with on this machine. The second means the update path works with no
/// setup at all for someone who already has that tool, and no token is stored by this
/// application unless they choose to give it one.
/// </remarks>
public sealed class UpdateService(HttpClient httpClient, UpdateOptions options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Reads what the running build says about itself.</summary>
    /// <remarks>
    /// Packaging writes this file; a build run from source has none, and reports itself
    /// unknown rather than inventing a version. An unknown build is never told it is out of
    /// date, because there is nothing to compare.
    /// </remarks>
    public BuildIdentity ReadInstalled()
    {
        try
        {
            var path = Path.Combine(options.InstallDirectory, "BUILD_INFO.txt");
            if (!File.Exists(path))
            {
                return BuildIdentity.Unknown;
            }

            var fields = File.ReadAllLines(path)
                .Select(line => line.Split('=', 2))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);
            return new(
                fields.GetValueOrDefault("version", "unknown"),
                fields.GetValueOrDefault("commit"),
                DateTimeOffset.TryParse(
                    fields.GetValueOrDefault("built_utc"),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal,
                    out var built)
                    ? built
                    : null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return BuildIdentity.Unknown;
        }
    }

    public async Task<UpdateCheck> CheckAsync(CancellationToken cancellationToken)
    {
        var installed = ReadInstalled();
        if (ResolveToken() is not { } token)
        {
            return new(
                UpdateAvailability.Unknown,
                installed,
                null,
                "No credential is available to read the update feed. Sign in with the GitHub CLI, "
                    + "or put a read-only token in the update settings.");
        }

        try
        {
            _assets = null;
            using var releaseResponse = await SendAsync(options.ReleaseUri, token, "application/vnd.github+json", cancellationToken)
                .ConfigureAwait(false);
            if (!releaseResponse.IsSuccessStatusCode)
            {
                return new(
                    UpdateAvailability.Unknown,
                    installed,
                    null,
                    $"The update feed answered {(int)releaseResponse.StatusCode}. Nothing was downloaded.");
            }

            var release = await releaseResponse.Content
                .ReadFromJsonAsync<Release>(Json, cancellationToken)
                .ConfigureAwait(false);
            var assets = release?.Assets ?? [];
            _assets = assets.ToDictionary(asset => asset.Name, asset => asset.Id, StringComparer.OrdinalIgnoreCase);
            if (!_assets.TryGetValue("update.json", out var manifestId))
            {
                return new(UpdateAvailability.Unknown, installed, null, "The published release carries no manifest.");
            }

            using var manifestResponse = await SendAsync(options.AssetUri(manifestId), token, "application/octet-stream", cancellationToken)
                .ConfigureAwait(false);
            manifestResponse.EnsureSuccessStatusCode();
            var published = await manifestResponse.Content
                .ReadFromJsonAsync<PublishedBuild>(Json, cancellationToken)
                .ConfigureAwait(false);
            if (published is null || string.IsNullOrWhiteSpace(published.Commit))
            {
                return new(UpdateAvailability.Unknown, installed, null, "The update feed returned nothing usable.");
            }

            // Commit rather than version, because the version is fixed at 1.0.0 and every build
            // of it is different. Two builds of the same commit are the same build.
            if (string.Equals(published.Commit, installed.Commit, StringComparison.OrdinalIgnoreCase))
            {
                return new(
                    UpdateAvailability.UpToDate,
                    installed,
                    published,
                    $"Up to date. Running {installed.ShortCommit}, built {Describe(installed.BuiltUtc)}.");
            }

            return new(
                UpdateAvailability.Available,
                installed,
                published,
                $"A newer verified build is available: {Short(published.Commit)}, built {Describe(published.BuiltUtc)}. "
                    + $"Running {installed.ShortCommit}.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new(UpdateAvailability.Unknown, installed, null, $"The update check failed: {exception.Message}");
        }
    }

    /// <summary>
    /// Downloads the offered build and proves it is the one that was published.
    /// </summary>
    /// <remarks>
    /// The hash is checked before anything is unpacked, and a mismatch throws the file away
    /// rather than keeping it for a person to look at. An update that cannot be proven is not
    /// an update.
    /// </remarks>
    public async Task<string> DownloadAsync(PublishedBuild published, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(published);
        if (ResolveToken() is not { } token)
        {
            throw new InvalidOperationException("No credential is available to download the update.");
        }

        if (_assets is null || !_assets.TryGetValue(published.Asset, out var assetId))
        {
            throw new InvalidOperationException("Check for updates before downloading one.");
        }

        Directory.CreateDirectory(options.StagingDirectory);
        var target = Path.Combine(options.StagingDirectory, published.Asset);
        using var response = await SendAsync(
                options.AssetUri(assetId),
                token,
                "application/octet-stream",
                cancellationToken,
                HttpCompletionOption.ResponseHeadersRead)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var destination = File.Create(target))
        {
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }

        var actual = await HashAsync(target, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actual, published.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(target);
            throw new InvalidOperationException(
                $"The downloaded build does not match its published checksum. Expected {published.Sha256}, got {actual}.");
        }

        return target;
    }

    /// <summary>Asset ids from the last check, so a download addresses the same release.</summary>
    private Dictionary<string, long>? _assets;

    private Task<HttpResponseMessage> SendAsync(
        Uri uri,
        string token,
        string accept,
        CancellationToken cancellationToken,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new(accept));
        // GitHub rejects a request with no user agent.
        request.Headers.UserAgent.Add(new("TarkovCompanion", "1.0.0"));
        return httpClient.SendAsync(request, completion, cancellationToken);
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Finds a credential, preferring one configured here over one borrowed from the CLI.
    /// </summary>
    /// <remarks>
    /// Borrowing the GitHub CLI's token means this works immediately on a machine that already
    /// has it signed in, and that this application stores no secret of its own. It is a
    /// fallback rather than the primary, so a deliberate token always wins.
    /// </remarks>
    private string? ResolveToken()
    {
        if (!string.IsNullOrWhiteSpace(options.Token))
        {
            return options.Token;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo("gh", "auth token")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            return process.WaitForExit(5000) && process.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private static string Short(string commit) => commit.Length >= 7 ? commit[..7] : commit;

    private static string Describe(DateTimeOffset? moment) => moment is { } value
        ? value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
        : "at an unknown time";
}
