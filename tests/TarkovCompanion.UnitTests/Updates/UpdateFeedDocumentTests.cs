using TarkovCompanion.App.Services.Updates;

namespace TarkovCompanion.UnitTests.Updates;

public sealed class UpdateFeedDocumentTests
{
    /// <summary>The shape the packaging tool writes, including fields this reader has no use for.</summary>
    private const string Published = """
        {
          "Assets": [
            {
              "PackageId": "TarkovCompanionDesktop",
              "Version": "1.0.1301",
              "Type": "Full",
              "FileName": "TarkovCompanionDesktop-1.0.1301-full.nupkg",
              "SHA1": "0B1D5C3F0C1D7C3C1A5B6E0F3A8B9C7D6E5F4A3B",
              "SHA256": "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
              "Size": 94371840,
              "NotesMarkdown": "",
              "SomethingAddedLater": { "nested": true }
            },
            {
              "PackageId": "TarkovCompanionDesktop",
              "Version": "1.0.1301",
              "Type": "Delta",
              "FileName": "TarkovCompanionDesktop-1.0.1301-delta.nupkg",
              "SHA256": "2C26B46B68FFC68FF99B453C1D30413413422D706483BFA0F98A5E886266E7AE",
              "Size": 1024
            }
          ]
        }
        """;

    [Fact]
    public void ThePublishedFeedParsesAndCarriesEveryPackagesHash()
    {
        var feed = UpdateFeedDocument.Parse(Published);

        Assert.Equal(2, feed.Packages.Count);
        var full = Assert.Single(feed.Packages, package => package.IsFull);
        Assert.Equal("TarkovCompanionDesktop", full.PackageId);
        Assert.Equal("1.0.1301", full.Version);
        Assert.Equal("TarkovCompanionDesktop-1.0.1301-full.nupkg", full.FileName);
        Assert.Equal(94371840, full.Size);
        // One case, so a comparison against a computed hash cannot fail on casing alone.
        Assert.Equal("9F86D081884C7D659A2FEAA0C55AD015A3BF4F1B2B0B822CD15D6C15B0F00A08", full.Sha256);
        Assert.Same(full, feed.Find(full.FileName));
        Assert.Null(feed.Find("something-else.nupkg"));
    }

    [Fact]
    public void AnEmptyFeedIsAFeedWithNothingInIt()
    {
        Assert.Empty(UpdateFeedDocument.Parse("""{"Assets":[]}""").Packages);
    }

    /// <summary>
    /// The updater library would fall back to SHA1 here. This channel has no signature, so the
    /// SHA256 in the feed is the only statement about the bytes, and a feed without it is refused.
    /// </summary>
    [Fact]
    public void APackageWithoutASha256RefusesTheWholeFeed()
    {
        var json = Published.Replace(
            "\"SHA256\": \"9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08\",",
            string.Empty,
            StringComparison.Ordinal);

        var refusal = Assert.Throws<UpdateFeedException>(() => UpdateFeedDocument.Parse(json));
        Assert.Contains("SHA256", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../evil.nupkg")]
    [InlineData("folder/evil.nupkg")]
    [InlineData("..\\evil.nupkg")]
    [InlineData("C:\\evil.nupkg")]
    [InlineData("a..b.nupkg")]
    [InlineData("")]
    public void AFileNameThatIsNotAPlainFileNameIsRefused(string fileName)
    {
        var json = Published.Replace(
            "TarkovCompanionDesktop-1.0.1301-full.nupkg",
            fileName.Replace("\\", "\\\\", StringComparison.Ordinal),
            StringComparison.Ordinal);

        Assert.Throws<UpdateFeedException>(() => UpdateFeedDocument.Parse(json));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"Assets":{}}""")]
    [InlineData("""{"Assets":["text"]}""")]
    [InlineData("""{"Assets":[{"PackageId":"a","Version":"1.0.0","FileName":"a.nupkg","SHA256":"abc","Size":1}]}""")]
    [InlineData("""{"Assets":[{"PackageId":"a","Version":"1.0.0","FileName":"a.nupkg","SHA256":"9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08","Size":0}]}""")]
    public void AMalformedFeedFailsClearlyRatherThanHalfParsing(string json)
    {
        Assert.Throws<UpdateFeedException>(() => UpdateFeedDocument.Parse(json));
    }
}
