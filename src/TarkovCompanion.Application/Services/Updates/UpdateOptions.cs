namespace TarkovCompanion.Application.Services.Updates;

/// <summary>
/// Where updates come from and where they are staged.
/// </summary>
/// <remarks>
/// The feed is one rolling pre-release tag rather than a tag per build. A tag per build would
/// accumulate a release for every commit on a branch that sees dozens in an evening, and the
/// application only ever wants the newest verified one.
/// </remarks>
/// <param name="Repository">The owner/name the release lives under.</param>
/// <param name="Tag">The rolling tag the verification job replaces.</param>
/// <param name="InstallDirectory">Where the running build lives.</param>
/// <param name="StagingDirectory">Where a downloaded build is kept before it is applied.</param>
/// <param name="Token">
/// A credential for a private repository. Left empty by default, in which case the GitHub
/// CLI's own sign-in is borrowed and this application stores no secret.
/// </param>
public sealed record UpdateOptions(
    string Repository,
    string Tag,
    string InstallDirectory,
    string StagingDirectory,
    string? Token = null)
{
    /// <summary>The release itself, which lists its assets and their ids.</summary>
    public Uri ReleaseUri => new($"https://api.github.com/repos/{Repository}/releases/tags/{Tag}");

    /// <summary>
    /// Addresses an asset by its id through the API rather than by its browser URL.
    /// </summary>
    /// <remarks>
    /// There is no endpoint that fetches an asset by name, so the release is read first and
    /// the name looked up in it. The browser download URL is not used: on a private repository
    /// it redirects to a signed location that rejects an Authorization header, and the API
    /// route accepts one.
    /// </remarks>
    public Uri AssetUri(long assetId) => new(
        $"https://api.github.com/repos/{Repository}/releases/assets/{assetId}");

    public static UpdateOptions CreateDefault(string installDirectory, string cacheRoot) => new(
        "smartpbx/tarkov-companion",
        "dev",
        installDirectory,
        Path.Combine(cacheRoot, "Updates"));
}
