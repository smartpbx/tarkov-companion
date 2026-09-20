using System.Text.RegularExpressions;
using TarkovCompanion.Application.Services.Strategy;

namespace TarkovCompanion.Application.Services.Raids;

/// <summary>
/// The game build, read from the name of the newest log folder the game has made.
/// </summary>
/// <remarks>
/// The game writes one folder per launch, <c>log_&lt;stamp&gt;_&lt;version&gt;</c>, and the version is
/// the same build string the logs carry everywhere else (docs/research/EFT_LOG_FACTS.md). Only the
/// folder's name is read, never a file in it, so this adds no log content to anything. It answers
/// null when there is no log root or no folder named that way: traffic compatibility names the exact
/// game version, so "not known" has to stay an answer rather than become a guess.
///
/// The answer is remembered for half a minute because the cockpit asks on every rebuild and the
/// folder changes once per game launch.
/// </remarks>
public sealed partial class EftLogFolderGameVersionSource : IGameVersionSource
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    private readonly Func<CancellationToken, Task<string?>> _logRoot;
    private readonly TimeProvider _timeProvider;
    private DateTimeOffset _readAtUtc = DateTimeOffset.MinValue;
    private string? _version;

    public EftLogFolderGameVersionSource(Func<CancellationToken, Task<string?>> logRoot, TimeProvider? timeProvider = null)
    {
        _logRoot = logRoot ?? throw new ArgumentNullException(nameof(logRoot));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<string?> GetAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        if (now - _readAtUtc < Lifetime)
        {
            return _version;
        }

        var version = Read(await _logRoot(cancellationToken).ConfigureAwait(false));
        _version = version;
        _readAtUtc = now;
        return version;
    }

    /// <summary>The version in a folder name such as <c>log_2026.09.10_12-00-00_1.1.5.0.47242</c>.</summary>
    public static string? ParseFolderName(string? name)
    {
        var match = name is null ? Match.Empty : FolderName().Match(name);
        return match.Success ? match.Groups["version"].Value : null;
    }

    private static string? Read(string? logRoot)
    {
        if (string.IsNullOrWhiteSpace(logRoot) || !Directory.Exists(logRoot))
        {
            return null;
        }

        try
        {
            return new DirectoryInfo(logRoot)
                .EnumerateDirectories("log_*")
                .OrderByDescending(folder => folder.LastWriteTimeUtc)
                .Select(folder => ParseFolderName(folder.Name))
                .FirstOrDefault(version => version is not null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^log_.+_(?<version>\d+(?:\.\d+){2,})$", RegexOptions.CultureInvariant)]
    private static partial Regex FolderName();
}
