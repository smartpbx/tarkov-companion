using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Infrastructure.Persistence;

namespace TarkovCompanion.IntegrationTests;

/// <summary>
/// Drives a whole raid through the real composition, from log line to summary.
/// </summary>
/// <remarks>
/// Raid end failed on a live machine and the logs could not say whether the fault was in the
/// status value or in the end-handling every status shares. Tonight's evidence could not tell
/// those apart, and waiting for the next scav raid to find out put the answer in somebody
/// else's hands. These tests settle it here: both endings are pushed through the same objects
/// the application builds at startup, with nothing stubbed between the parser and the summary.
///
/// Deliberately not a parser test. The parser is covered on its own; what was in doubt was
/// everything after it.
/// </remarks>
public sealed class RaidEndToEndTests
{
    private const string SelfProfileLine =
        "2026-09-11 22:00:00.000|1.1.5.0.47242|Info|application|SelectedProfile ProfileId: SELFPROFILE1 AccountId: 9041989";

    [Theory]
    [InlineData("Free")]
    [InlineData("Transfer")]
    public async Task ARaidThatStartsAndEndsReachesPostRaidWhateverTheEndingStatus(string status)
    {
        var root = TemporaryRoot();
        try
        {
            await using var services = await BuildAsync(root);
            var parser = services.GetRequiredService<EftLogParser>();
            var coordinator = services.GetRequiredService<RaidActivityCoordinator>();
            var store = services.GetRequiredService<IRuntimeStateStore>();
            parser.ParseLine(SelfProfileLine, DateTimeOffset.UtcNow);

            await ApplyAsync(parser, coordinator, Notification("userConfirmed", "Busy"));
            Assert.Equal(RaidLifecycleState.InRaid, store.Current.Raid.State);
            Assert.Equal("streets-of-tarkov", store.Current.Raid.MapId);

            await ApplyAsync(parser, coordinator, Notification("userMatchOver", status));

            Assert.Equal(RaidLifecycleState.PostRaid, store.Current.Raid.State);
            Assert.Equal("streets-of-tarkov", store.Current.Raid.MapId);
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>
    /// Every notification is written to two log files, so the watcher delivers each one twice.
    /// </summary>
    /// <remarks>
    /// The application reads both output and backend, and they carry the same events. A second
    /// copy of the ending must not undo the first or produce a second raid.
    /// </remarks>
    [Theory]
    [InlineData("Free")]
    [InlineData("Transfer")]
    public async Task ASecondCopyOfTheEndingChangesNothing(string status)
    {
        var root = TemporaryRoot();
        try
        {
            await using var services = await BuildAsync(root);
            var parser = services.GetRequiredService<EftLogParser>();
            var coordinator = services.GetRequiredService<RaidActivityCoordinator>();
            var store = services.GetRequiredService<IRuntimeStateStore>();
            parser.ParseLine(SelfProfileLine, DateTimeOffset.UtcNow);

            await ApplyAsync(parser, coordinator, Notification("userConfirmed", "Busy"));
            var raidId = store.Current.Raid.RaidId;
            await ApplyAsync(parser, coordinator, Notification("userConfirmed", "Busy"));
            Assert.Equal(raidId, store.Current.Raid.RaidId);

            await ApplyAsync(parser, coordinator, Notification("userMatchOver", status));
            await ApplyAsync(parser, coordinator, Notification("userMatchOver", status));

            Assert.Equal(RaidLifecycleState.PostRaid, store.Current.Raid.State);
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>
    /// The summary the player reads is produced, and reads correctly, for both endings.
    /// </summary>
    [Theory]
    [InlineData("Free")]
    [InlineData("Transfer")]
    public async Task TheSummaryAppearsWhenTheRaidEnds(string status)
    {
        var root = TemporaryRoot();
        try
        {
            await using var services = await BuildAsync(root);
            var viewModel = services.GetRequiredService<MainWindowViewModel>();
            var parser = services.GetRequiredService<EftLogParser>();
            var coordinator = services.GetRequiredService<RaidActivityCoordinator>();
            parser.ParseLine(SelfProfileLine, DateTimeOffset.UtcNow);

            await ApplyAsync(parser, coordinator, Notification("userConfirmed", "Busy"));
            Assert.False(viewModel.Raid.HasSummary);

            await ApplyAsync(parser, coordinator, Notification("userMatchOver", status));

            Assert.True(viewModel.Raid.HasSummary);
            var summary = viewModel.Raid.Summary;
            Assert.NotNull(summary);
            Assert.Contains("Streets", summary.Headline, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(RaidSummaryViewModel.OutcomeNotRecorded, summary.Outcome);
            // A transfer proves a scav run; Free leaves side to the profile, which here is the
            // signed-in one and so reads as PMC.
            Assert.Contains(
                status == "Transfer" ? "scav" : "PMC",
                summary.Side,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>
    /// Reported: "it just stays open right now" -- the summary used to close only via its own
    /// dismiss button or the next full history read.
    /// </summary>
    [Fact]
    public async Task TheSummaryClosesWhenTheNextRaidStarts()
    {
        var root = TemporaryRoot();
        try
        {
            await using var services = await BuildAsync(root);
            var viewModel = services.GetRequiredService<MainWindowViewModel>();
            var parser = services.GetRequiredService<EftLogParser>();
            var coordinator = services.GetRequiredService<RaidActivityCoordinator>();
            parser.ParseLine(SelfProfileLine, DateTimeOffset.UtcNow);

            await ApplyAsync(parser, coordinator, Notification("userConfirmed", "Busy", eventId: "E1"));
            await ApplyAsync(parser, coordinator, Notification("userMatchOver", "Free", eventId: "E1"));
            Assert.True(viewModel.Raid.HasSummary);

            // A different event id: the game's own confirmation that a second, distinct raid
            // has begun, not a second copy of the first one's notification.
            await ApplyAsync(parser, coordinator, Notification("userConfirmed", "Busy", eventId: "E2"));

            Assert.False(viewModel.Raid.HasSummary);
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>The summary closes on its own after fifteen minutes, whatever else happens.</summary>
    [Fact]
    public async Task TheSummaryClosesFifteenMinutesAfterItAppears()
    {
        var root = TemporaryRoot();
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 9, 17, 20, 0, 0, TimeSpan.Zero));
        try
        {
            await using var services = await BuildAsync(root, time);
            var viewModel = services.GetRequiredService<MainWindowViewModel>();
            var parser = services.GetRequiredService<EftLogParser>();
            var coordinator = services.GetRequiredService<RaidActivityCoordinator>();
            parser.ParseLine(SelfProfileLine, time.GetUtcNow());

            await ApplyAsync(parser, coordinator, Notification("userConfirmed", "Busy"), time);
            await ApplyAsync(parser, coordinator, Notification("userMatchOver", "Free"), time);
            Assert.True(viewModel.Raid.HasSummary);

            time.Advance(TimeSpan.FromMinutes(14));
            viewModel.Raid.Tick(time.GetUtcNow());
            Assert.True(viewModel.Raid.HasSummary);

            time.Advance(TimeSpan.FromMinutes(2));
            viewModel.Raid.Tick(time.GetUtcNow());
            Assert.False(viewModel.Raid.HasSummary);
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>
    /// The active-extracts panel is only about the raid running right now, so it must not still
    /// be showing the finished raid's confirmed exits once that raid has ended.
    /// </summary>
    [Fact]
    public async Task ActiveExtractsClearOnceTheRaidEnds()
    {
        var root = TemporaryRoot();
        try
        {
            await using var services = await BuildAsync(root);
            var viewModel = services.GetRequiredService<MainWindowViewModel>();
            var parser = services.GetRequiredService<EftLogParser>();
            var coordinator = services.GetRequiredService<RaidActivityCoordinator>();
            parser.ParseLine(SelfProfileLine, DateTimeOffset.UtcNow);

            await ApplyAsync(parser, coordinator, Notification("userConfirmed", "Busy"));
            await coordinator.ApplyExtractsAsync(
                [new ActiveExtract("extract:1", "Crossroads", new Confidence(0.9), "extracts-scan")],
                DateTimeOffset.UtcNow,
                TestContext.Current.CancellationToken,
                linesNotMatched: ["Unreadable line"],
                transits: ["Factory"]);

            Assert.True(viewModel.Raid.HasExtracts);
            Assert.True(viewModel.Raid.HasExtractsNotMatched);
            Assert.True(viewModel.Raid.HasTransits);

            await ApplyAsync(parser, coordinator, Notification("userMatchOver", "Free"));

            Assert.False(viewModel.Raid.HasExtracts);
            Assert.False(viewModel.Raid.HasExtractsNotMatched);
            Assert.False(viewModel.Raid.HasTransits);
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static async Task ApplyAsync(
        EftLogParser parser,
        RaidActivityCoordinator coordinator,
        string line,
        TimeProvider? time = null)
    {
        var evidence = parser.ParseLine(line, time?.GetUtcNow() ?? DateTimeOffset.UtcNow);
        Assert.NotNull(evidence);
        await coordinator.ApplyEvidenceAsync(evidence, TestContext.Current.CancellationToken);
    }

    private static string Notification(string type, string status, string eventId = "E1") =>
        "2026-09-12 01:23:54.000|1.1.5.0.47242|Info|backend|NOTIFICATION [EVENTID] " + type + " " +
        $$"""[{"type":"{{type}}","eventId":"{{eventId}}","profileid":"SELFPROFILE1","location":"TarkovStreets","status":"{{status}}"}]""";

    /// <summary>A clock the test moves by hand, so a fifteen-minute timeout does not cost fifteen minutes.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now += amount;
    }

    /// <summary>
    /// Builds the application's own container, with its database schema in place.
    /// </summary>
    /// <remarks>
    /// The migrations have to run. Without them the first raid start reaches the profile
    /// table, fails on a table that does not exist, and the whole exercise proves nothing.
    ///
    /// That mistake nearly went unnoticed, because the exception was raised inside the try
    /// block and then thrown away by a teardown that failed in the finally. The test reported
    /// the teardown fault, the real one never surfaced, and it read as a pass with a messy
    /// cleanup. Teardown that can throw is how a failing test disguises itself, which is a
    /// second reason the scratch directory helper now refuses to.
    /// </remarks>
    private static async Task<ServiceProvider> BuildAsync(string root, TimeProvider? time = null)
    {
        // Demo mode is how every other test builds the window view model headlessly. It
        // changes nothing this exercises: the raid state service only treats it as permission
        // to accept simulator evidence, and everything here is an ordinary log line.
        var services = AppComposition.Build(
            new AppCommandLine(false, true, true, false, null, null, null),
            new(DataRoot: root, Offline: true, TimeProvider: time));
        await services.GetRequiredService<SqliteMigrationRunner>()
            .ApplyAsync(TestContext.Current.CancellationToken);
        return services;
    }

    private static string TemporaryRoot() =>
        Path.Combine(Path.GetTempPath(), $"tarkov-raid-e2e-{Guid.NewGuid():N}");

    private static void Cleanup(string root) => ScratchDirectory.Remove(root);
}
