namespace TarkovCompanion.Application.Services.Raids;

/// <summary>
/// Folders the player named by hand, when the ones the companion guesses are wrong.
/// </summary>
/// <remarks>
/// Where the game keeps its screenshots depends on the install, on whether Documents has been
/// redirected into OneDrive, and on what the player set in the game itself. The candidate list
/// covers the arrangements we know of and will never cover all of them: the first person to
/// install this who does not use OneDrive had no screenshots detected and no way to say where
/// they were.
///
/// So there is a way to say. A named folder that exists wins outright over everything
/// discovery found, because somebody who has typed a path has answered the question the
/// guessing was for.
/// </remarks>
/// <param name="ScreenshotRoot">Where the game writes screenshots, or null to keep guessing.</param>
/// <param name="LogRoot">Where the game writes its per-launch log folders, or null to keep guessing.</param>
public sealed record EftPathOverrides(string? ScreenshotRoot, string? LogRoot)
{
    public static EftPathOverrides None { get; } = new(null, null);

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(ScreenshotRoot) && string.IsNullOrWhiteSpace(LogRoot);

    /// <summary>Trimmed, with blanks read as "not set" rather than as an empty path.</summary>
    public EftPathOverrides Normalized() => new(Clean(ScreenshotRoot), Clean(LogRoot));

    private static string? Clean(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : path.Trim();
}

/// <summary>Reads and writes the folders the player named.</summary>
public interface IEftPathOverrideStore
{
    Task<EftPathOverrides> GetAsync(CancellationToken cancellationToken);

    Task SaveAsync(EftPathOverrides overrides, CancellationToken cancellationToken);
}
