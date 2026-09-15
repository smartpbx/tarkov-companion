using TarkovCompanion.RecognitionCorpus;
using Xunit;

namespace TarkovCompanion.RecognitionCorpusTests;

/// <summary>
/// Checked-in fixture access shared by the corpus tests. Thresholds always come from the frozen
/// policy file in the repository, never from a copy rebuilt in test code, so a test cannot pass
/// against values the tool would refuse to load.
/// </summary>
internal static class CorpusFixtures
{
    public static readonly DateTimeOffset GoldenScoredUtc = new(2029, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly Lazy<FrozenThresholds> LoadedThresholds = new(() =>
    {
        var parsed = CorpusJson.ParseThresholds(ThresholdsJson());
        Assert.Empty(parsed.Errors);
        return Assert.IsType<FrozenThresholds>(parsed.Value);
    });

    public static string FixturePath(params string[] segments) =>
        Path.Join([AppContext.BaseDirectory, "fixtures", "recognition-corpus", .. segments]);

    public static string ThresholdsPath() => FixturePath("thresholds", "recognition-release.v1.json");

    public static string ThresholdsJson() => File.ReadAllText(ThresholdsPath());

    public static FrozenThresholds Thresholds() => LoadedThresholds.Value;

    /// <summary>Golden text with checkout line endings removed; the tool itself always writes LF.</summary>
    public static string Golden(string fileName) => File.ReadAllText(FixturePath("golden", fileName)).ReplaceLineEndings("\n");

    /// <summary>Fails with the complete actual text, so a deliberate contract change can be reviewed and re-pinned.</summary>
    public static void AssertGolden(string fileName, string actual)
    {
        if (!string.Equals(Golden(fileName), actual, StringComparison.Ordinal))
        {
            Assert.Fail($"Golden fixture {fileName} no longer matches. Complete actual output:\n{actual}");
        }
    }

    public static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Join(directory.FullName, ".git")) || File.Exists(Path.Join(directory.FullName, ".git")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The recognition corpus test repository root could not be found.");
    }

    /// <summary>A private scratch root in the system temporary directory, outside the repository.</summary>
    public static DirectoryInfo PrivateRoot() => Directory.CreateTempSubdirectory("recognition-corpus-");

    /// <summary>
    /// Link creation can need a privilege the account lacks. The helpers report that instead of
    /// throwing, and callers skip only the assertions that depend on the link.
    /// </summary>
    public static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            return false;
        }
    }

    public static bool TryCreateFileLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// A recursive delete removes links as entries and never follows them, which matters here:
    /// several private roots hold links whose targets are the checked-in worktree.
    /// </summary>
    public static void DeletePrivateRoot(DirectoryInfo root) => root.Delete(true);

    public sealed class FixedTime(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
