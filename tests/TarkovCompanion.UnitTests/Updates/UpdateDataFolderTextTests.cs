using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.Updates;

namespace TarkovCompanion.UnitTests.Updates;

public sealed class UpdateDataFolderTextTests
{
    /// <summary>
    /// An update replaces the application's folder. The data an installed build uses must not be
    /// in it, and the page must say the data is kept.
    /// </summary>
    [Fact]
    public void AnInstalledBuildsDataIsOutsideWhatAnUpdateReplaces()
    {
        var local = Path.Combine(Path.GetTempPath(), "LocalAppData");
        var installed = Path.Combine(local, RoughChannelHarness.PackId, "current");
        var data = AppDataPaths.Resolve(Path.Combine(local, "TarkovCompanion")).Root;

        Assert.False(UpdateDataFolderText.IsInside(data, Path.Combine(local, RoughChannelHarness.PackId)));
        var text = UpdateDataFolderText.Describe(data, installed);
        Assert.Contains(data, text, StringComparison.Ordinal);
        Assert.EndsWith("kept across updates", text, StringComparison.Ordinal);
    }

    /// <summary>The portable case, said honestly: the data goes wherever the folder goes, and no further.</summary>
    [Fact]
    public void APortableBuildsDataIsBesideItAndThePageSaysSo()
    {
        var folder = Path.Combine(Path.GetTempPath(), "v2-rough-7");
        var data = Path.Combine(folder, "Data");

        Assert.True(UpdateDataFolderText.IsInside(data, folder));
        Assert.EndsWith("stays with this folder", UpdateDataFolderText.Describe(data, folder), StringComparison.Ordinal);
    }

    [Fact]
    public void ASiblingFolderWithTheSamePrefixIsNotInside()
    {
        var local = Path.Combine(Path.GetTempPath(), "LocalAppData");

        Assert.False(UpdateDataFolderText.IsInside(
            Path.Combine(local, "TarkovCompanionDesktop"),
            Path.Combine(local, "TarkovCompanion")));
    }
}
