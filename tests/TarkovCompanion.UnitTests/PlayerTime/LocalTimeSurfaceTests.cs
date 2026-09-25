using System.Globalization;
using TarkovCompanion.App.Services.V2.SelfTest;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.UnitTests.V2Setup;
using TarkovCompanion.UnitTests.V2Shell;

namespace TarkovCompanion.UnitTests.PlayerTime;

/// <summary>
/// The screens Clayton reported: the self-test and its Copy diagnostics printed "... UTC" to a
/// player at UTC-4, so every absolute time on them read four hours out.
/// </summary>
/// <remarks>
/// Every probe is fed a known UTC instant under a zone that is never UTC (see
/// <see cref="PlayerClock"/>), so the expected text can only come from a real conversion. "Relative
/// stays relative": the ages ("2m ago") are computed from the two UTC instants and are untouched.
/// </remarks>
public sealed class LocalTimeSurfaceTests
{
    // 21:00 UTC is 17:00 on the wall of a player at UTC-4.
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 21, 0, 0, TimeSpan.Zero);
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
    private static readonly TimeSpan Took = TimeSpan.FromMilliseconds(120);

    [Fact]
    public void The_folder_probe_prints_local_change_times_and_no_utc_label()
    {
        using var pin = PlayerClock.Pin();

        var result = SelfTestProbes.Folders(SelfTestProbeTests.Folders(logsChanged: Now.AddMinutes(-2)), Now, Took, Culture);

        Assert.Contains(result.Facts, fact => fact.Text.Contains("last changed 2 min ago (2026-09-18 16:58)", StringComparison.Ordinal));
        Assert.Contains(result.Facts, fact => fact.Text.Contains("(2026-09-18 16:59)", StringComparison.Ordinal));
        Assert.Contains(result.Facts, fact => fact.Text.Contains("(2026-09-09 17:00)", StringComparison.Ordinal));
        Assert.All(result.Facts, fact => Assert.DoesNotContain("UTC", fact.Text, StringComparison.Ordinal));
    }

    [Fact]
    public void The_stale_log_folder_message_names_both_times_on_the_players_clock()
    {
        using var pin = PlayerClock.Pin();

        var result = SelfTestProbes.Folders(SelfTestProbeTests.Folders(logsChanged: Now.AddDays(-3)), Now, Took, Culture);

        var stale = Assert.Single(result.Facts, fact => fact.Text.Contains("stood still", StringComparison.Ordinal));
        Assert.Equal(
            "The log folder has stood still since 2026-09-15 17:00 while screenshots kept arriving until 2026-09-18 16:59 — the game is writing its logs somewhere else.",
            stale.Text);
    }

    [Fact]
    public void The_log_probe_prints_the_session_and_last_raid_on_the_players_clock()
    {
        using var pin = PlayerClock.Pin();

        var result = SelfTestProbes.Logs(SelfTestProbeTests.Logs(), Now, Took, Culture);

        // The session began 20:30 UTC and the last raid line was 20:56 UTC.
        Assert.Contains(result.Facts, fact => fact.Text.Contains("started 30 min ago (2026-09-18 16:30)", StringComparison.Ordinal));
        Assert.Contains(result.Facts, fact => fact.Text.Contains("last line was 4 min ago (2026-09-18 16:56)", StringComparison.Ordinal));
        Assert.All(result.Facts, fact => Assert.DoesNotContain("UTC", fact.Text, StringComparison.Ordinal));
    }

    [Fact]
    public void The_screenshot_probe_prints_when_the_game_wrote_it_and_when_it_was_read_locally()
    {
        using var pin = PlayerClock.Pin();

        var result = SelfTestProbes.Screenshots(SelfTestProbeTests.Screenshot(), Took, Culture);

        var wrote = Assert.Single(result.Facts, fact => fact.Text.Contains("The game wrote it at", StringComparison.Ordinal));
        Assert.Equal("The game wrote it at 2026-09-18 16:59:59, taken from the file's own write time", wrote.Text);
        Assert.Equal("read from the screenshot's own file at 17:00:00", wrote.Source);
    }

    /// <summary>
    /// The copied text is pasted to someone in another zone, so the header names which clock every
    /// time in it uses — once, as an offset, not as a "UTC" stuck on a local time.
    /// </summary>
    [Fact]
    public void Copy_diagnostics_starts_on_the_players_clock_and_names_the_offset_once()
    {
        using var pin = PlayerClock.Pin();
        var summary = new SelfTestSummary(
            Now,
            Took,
            [SelfTestProbes.Folders(SelfTestProbeTests.Folders(logsChanged: Now.AddMinutes(-2)), Now, Took, Culture)]);

        var text = summary.ToText(Culture);

        Assert.Contains("Started 2026-09-18 17:00:00 (UTC-04:00) · ", text, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\d UTC\b", text);
        Assert.Contains("(2026-09-18 16:58)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Copy_diagnostics_in_a_half_hour_zone_names_that_offset()
    {
        using var pin = PlayerClock.Pin(PlayerClock.UtcPlusFiveThirty);
        var summary = new SelfTestSummary(Now, Took, []);

        Assert.Contains("Started 2026-09-19 02:30:00 (UTC+05:30) · ", summary.ToText(Culture), StringComparison.Ordinal);
    }

    /// <summary>
    /// The party page: a queue that began at 03:00 UTC on the 19th was the evening of the 18th at
    /// UTC-4. Rendering the stored UTC time would put it on the wrong side of midnight.
    /// </summary>
    [Fact]
    public void The_party_page_reads_the_queue_and_update_times_on_the_players_clock()
    {
        using var pin = PlayerClock.Pin();
        var queued = new DateTimeOffset(2026, 9, 19, 3, 0, 0, TimeSpan.Zero);
        var updated = new DateTimeOffset(2026, 9, 19, 3, 3, 39, TimeSpan.Zero);
        var viewModel = new SquadPageViewModel(new NoItems());

        viewModel.Apply(V2ShellTestData.Snapshot() with { Squad = new SquadSnapshot([], queued, null, updated) });

        Assert.Equal("Queued 23:00:00", viewModel.Queue);
        Assert.Equal("Updated 23:03:39", viewModel.Evidence);
    }

    private sealed class NoItems : IItemRepository
    {
        public Task<ItemDefinition?> GetAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult<ItemDefinition?>(null);

        public Task<IReadOnlyList<ItemSearchHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ItemSearchHit>>([]);

        public Task<ItemPriceSnapshot?> GetPriceAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult<ItemPriceSnapshot?>(null);
    }
}
