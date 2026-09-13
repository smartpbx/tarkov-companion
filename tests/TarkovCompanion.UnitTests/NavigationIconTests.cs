using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// The navigation icons are one set, not fourteen characters that happened to be available.
/// </summary>
/// <remarks>
/// They used to be Unicode characters picked by eye from unrelated blocks, so each resolved to
/// a different fallback font and rendered at its own size, weight and baseline. The rail looked
/// ragged and the cause was invisible in the markup, because nothing about the code said these
/// were meant to match.
/// </remarks>
public sealed class NavigationIconTests
{
    [Fact]
    public async Task EveryDestinationHasDrawnGeometryRatherThanALetter()
    {
        // The container holds services that are only IAsyncDisposable, so a synchronous
        // dispose throws rather than cleaning up.
        await using var services = AppComposition.Build(AppCommandLine.Parse(["--demo"]));
        var viewModel = services.GetRequiredService<MainWindowViewModel>();

        Assert.NotEmpty(viewModel.Navigation);
        foreach (var item in viewModel.Navigation)
        {
            // Path geometry, which renders identically everywhere, rather than a character
            // whose appearance depends on which font a machine happens to fall back to.
            Assert.StartsWith("M", item.Glyph, StringComparison.Ordinal);
            Assert.True(item.Glyph.Length > 8, $"{item.Name} has no real geometry.");
        }
    }

    [Fact]
    public async Task NoTwoDestinationsShareAnIcon()
    {
        await using var services = AppComposition.Build(AppCommandLine.Parse(["--demo"]));
        var viewModel = services.GetRequiredService<MainWindowViewModel>();

        var glyphs = viewModel.Navigation.Select(item => item.Glyph).ToArray();

        Assert.Equal(glyphs.Length, glyphs.Distinct(StringComparer.Ordinal).Count());
    }
}
