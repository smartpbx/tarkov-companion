using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Infrastructure.Settings;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// The folders a player names when the guessing is wrong.
/// </summary>
/// <remarks>
/// Reported by the first person to install this who does not use OneDrive: no screenshots
/// detected, and no way at all to say where they were.
/// </remarks>
public sealed class EftPathOverrideStoreTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("tarkov-folders").FullName;

    [Fact]
    public async Task NothingStoredMeansKeepGuessing()
    {
        var stored = await Store().GetAsync(CancellationToken.None);

        Assert.True(stored.IsEmpty);
        Assert.Null(stored.ScreenshotRoot);
        Assert.Null(stored.LogRoot);
    }

    [Fact]
    public async Task WhatWasTypedComesBack()
    {
        var store = Store();
        await store.SaveAsync(new("D:\\EFT\\Screenshots", "D:\\EFT\\Logs"), CancellationToken.None);

        var stored = await Store().GetAsync(CancellationToken.None);

        Assert.Equal("D:\\EFT\\Screenshots", stored.ScreenshotRoot);
        Assert.Equal("D:\\EFT\\Logs", stored.LogRoot);
        Assert.False(stored.IsEmpty);
    }

    /// <summary>A box somebody emptied means "go back to guessing", not an empty path.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankBoxClearsTheOverride(string typed)
    {
        var store = Store();
        await store.SaveAsync(new("D:\\EFT\\Screenshots", null), CancellationToken.None);
        await store.SaveAsync(new(typed, typed), CancellationToken.None);

        Assert.True((await Store().GetAsync(CancellationToken.None)).IsEmpty);
    }

    [Fact]
    public async Task SurroundingSpaceIsNotPartOfThePath()
    {
        var store = Store();
        await store.SaveAsync(new("  D:\\EFT\\Screenshots  ", null), CancellationToken.None);

        Assert.Equal("D:\\EFT\\Screenshots", (await store.GetAsync(CancellationToken.None)).ScreenshotRoot);
    }

    /// <summary>
    /// A corrupt file costs the player their override, not their raid tracking.
    /// </summary>
    [Fact]
    public async Task AnUnreadableFileFallsBackToGuessing()
    {
        await File.WriteAllTextAsync(Path.Combine(_folder, "game-folders.json"), "{ not json", CancellationToken.None);

        Assert.True((await Store().GetAsync(CancellationToken.None)).IsEmpty);
    }

    private JsonFileEftPathOverrideStore Store() =>
        new(Path.Combine(_folder, "game-folders.json"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
