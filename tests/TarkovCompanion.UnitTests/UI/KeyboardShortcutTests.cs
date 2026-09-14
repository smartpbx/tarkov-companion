using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.UnitTests.UI;

/// <summary>
/// What the keyboard can reach, now that anything can.
/// </summary>
/// <remarks>
/// There was no KeyBinding, HotKey or KeyGesture anywhere in the application, and the player
/// arrives by alt-tab from a fullscreen game with both hands already on the keyboard.
///
/// The keystroke-to-handler wiring lives in the window and needs one to exist, so what is tested
/// here is what the keys actually do: the navigation a digit performs and the order Escape
/// undoes things in. Both are the parts that can be wrong in a way nobody notices.
/// </remarks>
public sealed class KeyboardShortcutTests
{
    [Fact]
    public async Task A_digit_goes_to_the_page_in_that_position()
    {
        await using var services = CreateServices();
        var viewModel = services.GetRequiredService<MainWindowViewModel>();

        Assert.True(viewModel.NavigateTo(0));
        Assert.Equal("Raid", Assert.Single(viewModel.Navigation, item => item.IsSelected).Name);

        Assert.True(viewModel.NavigateTo(3));
        Assert.Equal("Scanner", Assert.Single(viewModel.Navigation, item => item.IsSelected).Name);
    }

    [Fact]
    public async Task A_digit_past_the_last_page_does_nothing_rather_than_something_else()
    {
        // Fourteen pages and ten digits means four the keyboard cannot reach. A digit that
        // silently went to the wrong page would be worse than one that goes nowhere.
        await using var services = CreateServices();
        var viewModel = services.GetRequiredService<MainWindowViewModel>();
        var before = viewModel.CurrentPage;

        Assert.False(viewModel.NavigateTo(99));
        Assert.False(viewModel.NavigateTo(-1));
        Assert.Same(before, viewModel.CurrentPage);
    }

    [Fact]
    public async Task Escape_with_nothing_to_undo_says_so()
    {
        // So the press is not marked handled and still reaches whatever else wanted it — a
        // dropdown, a dialog — rather than being swallowed by a handler that did nothing.
        await using var services = CreateServices();
        var viewModel = services.GetRequiredService<MainWindowViewModel>();

        Assert.False(viewModel.Dismiss());
    }

    [Fact]
    public async Task Escape_closes_an_open_replay()
    {
        await using var services = CreateServices();
        var viewModel = services.GetRequiredService<MainWindowViewModel>();
        viewModel.Raid.Replay.Open("A raid", [Somewhere]);
        Assert.True(viewModel.Raid.Replay.IsOpen);

        Assert.True(viewModel.Dismiss());
        Assert.False(viewModel.Raid.Replay.IsOpen);
    }

    [Fact]
    public async Task One_press_undoes_one_thing()
    {
        // Escape clearing three things at once is Escape losing two of them for somebody who
        // wanted the first. With a replay open and nothing else to undo, the first press takes
        // the replay and the second finds nothing left.
        await using var services = CreateServices();
        var viewModel = services.GetRequiredService<MainWindowViewModel>();
        viewModel.Raid.Replay.Open("A raid", [Somewhere]);

        Assert.True(viewModel.Dismiss());
        Assert.False(viewModel.Dismiss());
    }

    [Fact]
    public async Task An_empty_replay_was_never_open_to_close()
    {
        // Nothing to step through is nothing to close, and Escape should not report having
        // undone something it did not.
        await using var services = CreateServices();
        var viewModel = services.GetRequiredService<MainWindowViewModel>();
        viewModel.Raid.Replay.Open("A raid with no screenshots", []);

        Assert.False(viewModel.Raid.Replay.IsOpen);
        Assert.False(viewModel.Dismiss());
    }

    private static ScreenshotPosition Somewhere { get; } = new(
        DateTimeOffset.Parse("2026-09-13T20:00:00Z"),
        new(120, 3, 240),
        new(0, 0, 0, 1),
        90,
        null,
        null,
        "2026-09-13[20-00]_120.0, 3.0, 240.0_0.0, 0.0, 0.0, 1.0_12.34 (0).png");

    private static ServiceProvider CreateServices() => AppComposition.Build(
        new AppCommandLine(false, true, false, false, null, null, null),
        new(DataRoot: Path.Combine(Path.GetTempPath(), $"tarkov-keys-{Guid.NewGuid():N}"), Offline: true));
}
