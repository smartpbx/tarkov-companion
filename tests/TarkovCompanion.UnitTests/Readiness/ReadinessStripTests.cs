using TarkovCompanion.App.ViewModels.V2.Shell;
using TarkovCompanion.Application.Services.Readiness;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Situations;

namespace TarkovCompanion.UnitTests.Readiness;

/// <summary>[#712 1-13] When the Raid page's readiness strip shows, what it says, and what its buttons do.</summary>
public sealed class ReadinessStripTests
{
    private static readonly EftObservationState Looking = new(true, false, false, null, null, Confidence.Unknown, "Looking");
    private static readonly EftObservationState NotFound = Looking with { Searched = true };
    private static readonly EftObservationState Watching = NotFound with
    {
        IsWatchingLogs = true,
        IsWatchingScreenshots = true,
        LogRoot = "C:/EFT/Logs",
        ScreenshotRoot = "C:/Docs/Escape from Tarkov/Screenshots",
    };
    private static readonly RuntimeDataState Empty = new(DataAvailability.Unavailable, 0, 0, null, string.Empty);
    private static readonly RuntimeDataState Stored = new(DataAvailability.Cached, 4200, 9, DateTimeOffset.UtcNow.AddDays(-3), string.Empty);

    private static ReadinessStripInput Input(
        EftObservationState? observation = null,
        RuntimeDataState? data = null,
        bool localOnly = false,
        bool sharing = false,
        FormatHealthReport? format = null,
        bool demo = false,
        bool databaseReady = true) =>
        new(demo, observation ?? Watching, databaseReady, data ?? Stored, localOnly, sharing, format);

    [Fact]
    public void An_existing_player_with_folders_and_a_stored_catalog_sees_no_strip()
    {
        // An old catalog still answers; only an empty one blocks. Squad off is optional.
        var state = ReadinessStripRules.Evaluate(Input());

        Assert.False(state.IsVisible);
        Assert.All(state.Items.Where(item => item.Required), item => Assert.Equal(ReadinessItemState.Ready, item.State));
        Assert.Equal(ReadinessItemState.Optional, state.For(ReadinessItemKind.Squad)!.State);
    }

    [Fact]
    public void Still_looking_and_still_downloading_do_not_flash_the_strip_at_startup()
    {
        var state = ReadinessStripRules.Evaluate(Input(Looking, Empty with { Availability = DataAvailability.Refreshing }));

        Assert.False(state.IsVisible);
        Assert.Equal(ReadinessItemState.Checking, state.For(ReadinessItemKind.GameLogs)!.State);
        Assert.Equal(ReadinessItemState.Checking, state.For(ReadinessItemKind.GameData)!.State);
        Assert.Equal(ReadinessItemState.Checking,
            ReadinessStripRules.Evaluate(Input(data: Empty, databaseReady: false)).For(ReadinessItemKind.GameData)!.State);
    }

    [Fact]
    public void First_run_with_nothing_found_shows_the_strip_with_a_folder_picker_for_each_folder()
    {
        var state = ReadinessStripRules.Evaluate(Input(NotFound, Empty with { Availability = DataAvailability.Refreshing }));

        Assert.True(state.IsVisible);
        Assert.Equal(
            [ReadinessItemKind.GameLogs, ReadinessItemKind.Screenshots, ReadinessItemKind.GameData, ReadinessItemKind.Squad],
            state.Items.Select(item => item.Kind));
        Assert.Equal(ReadinessFix.ChooseLogFolder, state.For(ReadinessItemKind.GameLogs)!.Fix);
        Assert.Equal(ReadinessFix.ChooseScreenshotFolder, state.For(ReadinessItemKind.Screenshots)!.Fix);
        Assert.Equal(ReadinessFix.None, state.For(ReadinessItemKind.GameData)!.Fix);
    }

    [Fact]
    public void Screenshots_alone_missing_keeps_the_strip_and_names_only_that()
    {
        var state = ReadinessStripRules.Evaluate(Input(Watching with { IsWatchingScreenshots = false, ScreenshotRoot = null }));

        Assert.True(state.IsVisible);
        Assert.Equal([ReadinessItemKind.Screenshots], state.Items.Where(item => item.Blocks).Select(item => item.Kind));
    }

    [Fact]
    public void A_failed_or_absent_download_offers_retry_and_Local_only_offers_the_switch_instead()
    {
        var failed = ReadinessStripRules.Evaluate(Input(data: Empty with { Availability = DataAvailability.Error }));
        var absent = ReadinessStripRules.Evaluate(Input(data: Empty));
        var localOnly = ReadinessStripRules.Evaluate(Input(data: Empty, localOnly: true));

        Assert.Equal((ReadinessItemState.Failed, ReadinessFix.RetryData), Of(failed.For(ReadinessItemKind.GameData)!));
        Assert.Equal((ReadinessItemState.Missing, ReadinessFix.RetryData), Of(absent.For(ReadinessItemKind.GameData)!));
        Assert.Equal((ReadinessItemState.LocalOnly, ReadinessFix.OpenDataNetwork), Of(localOnly.For(ReadinessItemKind.GameData)!));
        Assert.True(failed.IsVisible && absent.IsVisible && localOnly.IsVisible);

        // Local only with a catalog already stored: nothing is missing.
        Assert.False(ReadinessStripRules.Evaluate(Input(localOnly: true)).IsVisible);
    }

    [Fact]
    public void Local_only_follows_the_runtime_offline_flag_as_well_as_the_switch()
    {
        var snapshot = new ApplicationRuntimeSnapshot(false, true, true, Empty, null, new Core.Domain.Raids.RaidSnapshot(null, Core.Domain.Raids.RaidLifecycleState.Unknown, null, null, DateTimeOffset.UtcNow, Confidence.Unknown, null, [], false), ScanExecutionResult.Unavailable("x", DateTimeOffset.UtcNow))
        {
            Observation = Watching,
        };

        var state = ReadinessStripRules.Evaluate(ReadinessStripInput.From(snapshot, localOnly: false, format: null));

        Assert.Equal(ReadinessItemState.LocalOnly, state.For(ReadinessItemKind.GameData)!.State);
    }

    [Fact]
    public void A_changed_game_format_shows_the_strip_even_when_everything_else_is_green()
    {
        var degraded = new FormatHealthReport(
        [
            new(FormatSource.GameLog, FormatHealthStatus.Degraded, 2, 60, "1.1.6.0", "1.1.5.1", DateTimeOffset.UtcNow),
        ], "1.1.6.0");

        var state = ReadinessStripRules.Evaluate(Input(format: degraded));

        Assert.True(state.IsVisible);
        Assert.Equal((ReadinessItemState.Failed, ReadinessFix.OpenFormatHealth), Of(state.For(ReadinessItemKind.GameFormat)!));
        Assert.Null(ReadinessStripRules.Evaluate(Input(format: FormatHealthReport.Empty)).For(ReadinessItemKind.GameFormat));
    }

    [Fact]
    public void Squad_is_optional_and_never_keeps_the_strip_up()
    {
        var state = ReadinessStripRules.Evaluate(Input(sharing: false));

        Assert.False(state.IsVisible);
        Assert.Equal(ReadinessFix.OpenSquad, state.For(ReadinessItemKind.Squad)!.Fix);
        Assert.Equal(ReadinessItemState.Ready, ReadinessStripRules.Evaluate(Input(sharing: true)).For(ReadinessItemKind.Squad)!.State);
    }

    [Fact]
    public void Demo_mode_and_a_machine_that_cannot_watch_the_game_show_nothing()
    {
        Assert.False(ReadinessStripRules.Evaluate(Input(NotFound, Empty, demo: true)).IsVisible);
        Assert.False(ReadinessStripRules.Evaluate(Input(NotFound with { IsSupported = false }, Empty)).IsVisible);
    }

    [Theory]
    [InlineData("game", "game/Logs")]
    [InlineData("game/Logs", "game/Logs")]
    [InlineData("elsewhere", "elsewhere")]
    public void A_picked_game_folder_is_saved_as_the_Logs_folder_inside_it(string picked, string expected)
    {
        var exists = new HashSet<string>(StringComparer.Ordinal) { Path.Combine("game", "Logs") };

        Assert.Equal(
            expected.Replace('/', Path.DirectorySeparatorChar),
            ChosenGameFolder.Resolve(ReadinessItemKind.GameLogs, picked.Replace('/', Path.DirectorySeparatorChar), exists.Contains));
    }

    [Fact]
    public async Task Choose_folder_saves_the_folder_meant_and_a_cancelled_picker_saves_nothing()
    {
        var saved = new List<(ReadinessItemKind, string)>();
        var run = new List<ReadinessFix>();
        var docs = Path.Combine("Docs", "Escape from Tarkov");
        var strip = new ReadinessStripViewModel(run.Add, (kind, folder) =>
        {
            saved.Add((kind, folder));
            return Task.CompletedTask;
        })
        {
            DirectoryExists = path => path == Path.Combine(docs, "Screenshots"),
        };
        string? answer = docs;
        strip.PickFolder = _ => Task.FromResult<string?>(answer);

        await strip.RunFixAsync(ReadinessFix.ChooseScreenshotFolder);
        answer = null;
        await strip.RunFixAsync(ReadinessFix.ChooseLogFolder);
        await strip.RunFixAsync(ReadinessFix.OpenDataNetwork);

        Assert.Equal([(ReadinessItemKind.Screenshots, Path.Combine(docs, "Screenshots"))], saved);
        Assert.Equal([ReadinessFix.OpenDataNetwork], run);
    }

    [Fact]
    public void The_strip_view_model_words_each_item_and_only_offers_buttons_that_fix_something()
    {
        var strip = new ReadinessStripViewModel(_ => { }, (_, _) => Task.CompletedTask);
        var changes = 0;
        strip.PropertyChanged += (_, _) => changes++;
        var state = ReadinessStripRules.Evaluate(Input(Watching with { IsWatchingScreenshots = false }, Empty with { Availability = DataAvailability.Error }));

        strip.Apply(state);
        var raised = changes;
        strip.Apply(ReadinessStripRules.Evaluate(Input(Watching with { IsWatchingScreenshots = false }, Empty with { Availability = DataAvailability.Error })));

        Assert.True(strip.IsVisible);
        Assert.Equal(
            ["Game logs ✓", "Screenshots not found [Choose folder…]", "Game data download failed [Retry]", "Squad (optional) [Set up]"],
            strip.Items.Select(item => $"{item.Label} {item.State}{(item.HasFix ? $" [{item.FixLabel}]" : string.Empty)}"));
        Assert.Equal(raised, changes);

        strip.Apply(ReadinessStripRules.Evaluate(Input()));
        Assert.False(strip.IsVisible);
    }

    private static (ReadinessItemState, ReadinessFix) Of(ReadinessItem item) => (item.State, item.Fix);
}
