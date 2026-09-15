using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.Services.V2.Shell;

namespace TarkovCompanion.WindowsSmokeTests.V2Shell;

/// <summary>
/// Pins the launch vocabulary used by the Windows packaged-capture lane.
/// </summary>
/// <remarks>
/// This is not rendering, usability, or accessibility evidence. The packaged gallery still has to
/// launch and capture legacy, v2-a, and v2-b on its exact commit, with every V2 image labelled
/// <c>provisional - #265 not yet run</c>.
/// </remarks>
public sealed class V2ShellLaunchEvidenceTests
{
    [Theory]
    [InlineData("legacy", V2ShellMode.Legacy)]
    [InlineData("v2-a", V2ShellMode.VariantA)]
    [InlineData("v2-b", V2ShellMode.VariantB)]
    public void PackagedLaunchModeIsDeterministic(string token, V2ShellMode expected) =>
        Assert.Equal(expected, AppCommandLine.Parse(["--ui-shell", token]).UiShell);

    [Fact]
    public void InvalidPackagedLaunchModeFailsInsteadOfFallingBack() =>
        Assert.Throws<ArgumentException>(() => AppCommandLine.Parse(["--ui-shell", "candidate-c"]));
}
