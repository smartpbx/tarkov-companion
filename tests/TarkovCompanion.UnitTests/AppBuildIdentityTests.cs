using System.Text.RegularExpressions;
using TarkovCompanion.App.Services.Diagnostics;

namespace TarkovCompanion.UnitTests;

public sealed class AppBuildIdentityTests
{
    /// <summary>
    /// The running build's version starts with whatever PRODUCT_VERSION says, and with nothing else.
    /// </summary>
    /// <remarks>
    /// The point of the test is the file, not the number. "1.0." was typed in front of the run
    /// number in eight places and never moved, so an installed V2 build called itself 1.0.1121.
    /// This build was compiled with no version passed in, so what it reports came from
    /// Directory.Build.props reading that one file. If the two ever disagree, a second copy of
    /// the number has appeared somewhere.
    /// </remarks>
    [Fact]
    public void TheRunningBuildTakesItsGenerationFromTheOneFileThatDecidesIt()
    {
        var product = File.ReadAllText(Path.Combine(RepositoryRoot(), "PRODUCT_VERSION")).Trim();
        Assert.Matches(@"^\d+\.\d+$", product);

        var current = AppBuildIdentity.Current;

        Assert.StartsWith(product + ".", current.Version, StringComparison.Ordinal);
        Assert.Matches(@"^\d+\.\d+\.\d+(-dev)?$", current.Version);
        Assert.Equal(int.Parse(product.Split('.')[0], System.Globalization.CultureInfo.InvariantCulture), current.Generation);
    }

    /// <summary>This is the V2 workspace. A build that says generation 1 is describing a different product.</summary>
    [Fact]
    public void TheProductGenerationIsTwo()
    {
        Assert.Equal(2, AppBuildIdentity.Current.Generation);
    }

    [Theory]
    [InlineData("2.0.1140+0123456789abcdef0123456789abcdef01234567", "2.0.1140", "0123456789abcdef0123456789abcdef01234567")]
    // What a CI build really carries: the workflow's commit, then the SDK's copy of it.
    [InlineData("2.0.1140+0123456789abcdef0123456789abcdef01234567.0123456789abcdef0123456789abcdef01234567", "2.0.1140", "0123456789abcdef0123456789abcdef01234567")]
    [InlineData("2.0.0-dev+abc", "2.0.0-dev", "abc")]
    [InlineData("2.0.0-dev", "2.0.0-dev", null)]
    [InlineData("2.0.1140+", "2.0.1140", null)]
    [InlineData("", "unknown", null)]
    [InlineData(null, "unknown", null)]
    public void AnInformationalVersionSplitsIntoTheVersionAndTheCommit(string? raw, string version, string? commit)
    {
        var identity = AppBuildIdentity.Parse(raw);

        Assert.Equal(version, identity.Version);
        Assert.Equal(commit, identity.Commit);
    }

    [Fact]
    public void AVersionWithNoNumberInFrontHasNoGeneration()
    {
        Assert.Null(AppBuildIdentity.Parse("unknown").Generation);
        Assert.Equal(1, AppBuildIdentity.Parse("1.0.1121+abc").Generation);
    }

    /// <summary>
    /// Nothing that builds, packages or verifies spells a version of its own.
    /// </summary>
    /// <remarks>
    /// Loose matching is how the old number survived: every assertion compared against a string
    /// that was itself typed beside it, so all of them agreed and all of them were wrong. These
    /// are the files that used to carry a copy.
    /// </remarks>
    [Theory]
    [InlineData(".github/workflows/windows-verify.yml")]
    [InlineData("scripts/package-windows.sh")]
    public void NoBuildScriptSpellsAVersionOfItsOwn(string relativePath)
    {
        var text = File.ReadAllText(Path.Combine(RepositoryRoot(), relativePath));
        var code = string.Join('\n', text.Split('\n').Where(line => !line.TrimStart().StartsWith('#')));

        Assert.DoesNotMatch(new Regex(@"\b\d+\.\d+\.\$\{\{\s*github\.run_number"), code);
        Assert.DoesNotMatch(new Regex(@"TarkovCompanion-v\d"), code);
        Assert.DoesNotMatch(new Regex(@"TARKOV_BUILD_VERSION:-\d"), code);
        Assert.Contains("TARKOV_BUILD_VERSION", code, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TarkovCompanion.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root was not found above the test output.");
    }
}
