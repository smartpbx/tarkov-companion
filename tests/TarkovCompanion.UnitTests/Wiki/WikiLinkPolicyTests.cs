using TarkovCompanion.Application.Services.Wiki;

namespace TarkovCompanion.UnitTests.Wiki;

public sealed class WikiLinkPolicyTests
{
    [Theory]
    [InlineData("https://escapefromtarkov.fandom.com/wiki/Bandage")]
    [InlineData("https://ESCAPEFROMTARKOV.FANDOM.COM/wiki/Bandage")]
    public void AllowsTheRealWikiHost(string url) => Assert.True(WikiLinkPolicy.IsAllowed(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://escapefromtarkov.fandom.com/wiki/Bandage")]
    [InlineData("https://evil.example.com/wiki/Bandage")]
    [InlineData("https://escapefromtarkov.fandom.com.evil.example.com/wiki/Bandage")]
    [InlineData("not a url")]
    [InlineData("javascript:alert(1)")]
    public void RejectsAnythingElse(string? url) => Assert.False(WikiLinkPolicy.IsAllowed(url));
}
