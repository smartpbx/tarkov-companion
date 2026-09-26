using System.Security.Cryptography;
using TarkovCompanion.App.Services.Updates;
using TarkovCompanion.App.ViewModels.V2.Setup;
using TarkovCompanion.Core.Common;

namespace TarkovCompanion.UnitTests.Updates;

/// <summary>#292: going back to the previous build, the pin that keeps it there, and provenance.</summary>
public sealed class RollbackTests
{
    private const string Pack = RoughChannelHarness.PackId;

    private static UpdateFeedPackage Full(string version, string id = Pack) =>
        new(id, version, true, $"{id}-{version}-full.nupkg", new string('A', 64), 10);

    [Fact]
    public void ThePreviousBuildIsTheNewestFullBuildOlderThanTheInstalledOne()
    {
        UpdateFeedPackage[] feed =
        [
            Full("1.0.300"),
            Full("1.0.100"),
            Full("1.0.150") with { IsFull = false, FileName = "delta.nupkg" },
            Full("1.0.120"),
            Full("1.0.180", id: "SomethingElse"),
            Full("1.0.200"),
        ];

        var chosen = UpdateRollbackRules.Choose("1.0.200", feed, Pack, kept: null);

        Assert.NotNull(chosen);
        Assert.Equal("1.0.120", chosen.Version);
        Assert.Equal(RollbackSource.Feed, chosen.Source);
        Assert.Equal("1.0.300", UpdateRollbackRules.Latest(feed, Pack));
    }

    [Fact]
    public void AKeptCopyIsChosenWhenTheFeedHasNothingOlderAndLosesATieToTheFeed()
    {
        var kept = new RollbackCandidate("1.0.100", "k.nupkg", new string('B', 64), 10, RollbackSource.KeptCopy);

        Assert.Equal(RollbackSource.KeptCopy, UpdateRollbackRules.Choose("1.0.200", [Full("1.0.200")], Pack, kept)!.Source);
        Assert.Equal(RollbackSource.Feed, UpdateRollbackRules.Choose("1.0.200", [Full("1.0.100")], Pack, kept)!.Source);
        Assert.Equal("1.0.150", UpdateRollbackRules.Choose("1.0.200", [Full("1.0.150")], Pack, kept)!.Version);
        Assert.Null(UpdateRollbackRules.Choose("1.0.200", [Full("1.0.200"), Full("1.0.300")], Pack, kept: null));
        Assert.Null(UpdateRollbackRules.Choose("not a version", [Full("1.0.100")], Pack, kept));
    }

    [Fact]
    public void APinHoldsBackEverythingUpToTheNewestKnownBuildAndNothingNewer()
    {
        var now = DateTimeOffset.UnixEpoch;
        var pin = UpdateRollbackRules.PinFor("1.0.100", installed: "1.0.200", latestInFeed: "1.0.300", now);
        Assert.Equal("1.0.300", pin.HoldThrough);
        // The build being left can be newer than the feed.
        Assert.Equal("1.0.200", UpdateRollbackRules.PinFor("1.0.100", "1.0.200", "1.0.150", now).HoldThrough);
        Assert.Equal("1.0.200", UpdateRollbackRules.PinFor("1.0.100", "1.0.200", null, now).HoldThrough);

        Assert.True(UpdateRollbackRules.Holds(pin, installed: "1.0.100", offered: "1.0.200"));
        Assert.True(UpdateRollbackRules.Holds(pin, installed: "1.0.100", offered: "1.0.300"));
        Assert.False(UpdateRollbackRules.Holds(pin, installed: "1.0.100", offered: "1.0.301"));
        // Going back did not apply, or an installer ran since: the pin is about another machine.
        Assert.False(UpdateRollbackRules.Holds(pin, installed: "1.0.200", offered: "1.0.300"));
    }

    [Fact]
    public void ProvenanceBelievesOnlyARecordThatNamesTheRunningBuild()
    {
        using var zone = LocalTime.UseZone(TimeZoneInfo.Utc);
        var applied = new DateTimeOffset(2026, 9, 23, 18, 42, 0, TimeSpan.Zero);
        var record = new UpdateProvenance("1.0.200", "Rough test builds", "tarkov.mannerow.net", new string('C', 64), applied, WentBack: true);
        var feed = new Uri("https://tarkov.mannerow.net/updates/rough/");

        var rows = UpdateProvenanceText.Rows("1.0.200", "Rough test builds", feed, record, feedSha256: null).ToDictionary(row => row.Label, row => row.Value);
        Assert.Equal("tarkov.mannerow.net", rows["Feed"]);
        Assert.Equal(new string('C', 64), rows["SHA-256"]);
        Assert.Equal($"{LocalTime.Moment(applied)} · went back", rows["Applied"]);

        var stale = UpdateProvenanceText.Rows("1.0.300", "Rough test builds", feed, record, feedSha256: new string('D', 64)).ToDictionary(row => row.Label, row => row.Value);
        Assert.Equal(UpdateProvenanceText.NotRecorded, stale["Applied"]);
        Assert.StartsWith(new string('D', 64), stale["SHA-256"], StringComparison.Ordinal);
        Assert.Equal("local folder", UpdateProvenanceText.HostOf(new Uri("file:///C:/feed/")));
    }

    /// <summary>
    /// The whole road back through the updater library itself: the older build is found in the
    /// feed, fetched, checked, handed over as a downgrade, and the pin then keeps the check quiet
    /// about the build that was left until a newer one is published.
    /// </summary>
    [Fact]
    public async Task GoingBackFromTheFeedHandsTheOlderPackageToTheUpdaterAndPinsIt()
    {
        using var state = new TemporaryState();
        using (var harness = new RoughChannelHarness(installedVersion: "1.0.200"))
        {
            harness.PublishAll("1.0.300", "1.0.100", "1.0.200");
            var gateway = GatewayOver(harness, state.Store);

            var offer = await gateway.FindPreviousAsync(CancellationToken.None);
            Assert.Equal("1.0.200", offer.Installed);
            Assert.Equal("1.0.100", offer.Previous?.Version);
            Assert.Equal(RollbackSource.Feed, offer.Previous?.Source);
            Assert.Equal("1.0.300", offer.LatestInFeed);

            var fetched = await gateway.DownloadPreviousAsync(offer, null, CancellationToken.None);
            Assert.True(fetched.CanApply, fetched.Status);
            var staged = Path.Combine(harness.Packages, offer.Previous!.FileName);
            Assert.Equal(offer.Previous.Sha256, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(staged))));

            // No pin until it is really handed over.
            Assert.Null(state.Store.Read().Pin);
            ((IUpdateRollback)gateway).ApplyAndRestart();

            var apply = Assert.Single(harness.Locator.Recorded.Started);
            Assert.Equal(staged, apply.Arguments[apply.Arguments.ToList().IndexOf("--package") + 1]);
            var remembered = state.Store.Read();
            Assert.Equal(new("1.0.100", "1.0.300", remembered.Pin!.SetUtc), remembered.Pin);
            Assert.Equal("1.0.100", remembered.Applied?.Version);
            Assert.True(remembered.Applied?.WentBack);
            Assert.Equal(offer.Previous.Sha256, remembered.Applied?.Sha256);
        }

        // Reopened on the older build: the build it left, and the newest one, are held back.
        using (var reopened = new RoughChannelHarness(installedVersion: "1.0.100"))
        {
            reopened.PublishAll("1.0.300", "1.0.200");
            var gateway = GatewayOver(reopened, state.Store);
            Assert.NotNull(gateway.Pin);

            var held = await gateway.CheckAsync(CancellationToken.None);
            Assert.False(held.CanDownload);
            Assert.True(held.Held);
            Assert.Contains("Staying on 1.0.100", held.Status, StringComparison.Ordinal);

            // The next build ends the pin and is offered.
            reopened.PublishAll("1.0.400", "1.0.300");
            var offered = await gateway.CheckAsync(CancellationToken.None);
            Assert.True(offered.CanDownload);
            Assert.Equal("1.0.400", offered.Available);
            Assert.Null(state.Store.Read().Pin);
        }
    }

    /// <summary>
    /// The rough feed lists one build, so the usual way back is the running build's package,
    /// kept out of <c>packages\</c> before an update deletes it.
    /// </summary>
    [Fact]
    public async Task AnUpdateKeepsTheRunningBuildsPackageAndGoingBackUsesIt()
    {
        using var state = new TemporaryState();
        var installedBytes = RandomNumberGenerator.GetBytes(32 * 1024);
        var keptName = $"{Pack}-1.0.100-full.nupkg";
        using (var harness = new RoughChannelHarness(installedVersion: "1.0.100"))
        {
            await File.WriteAllBytesAsync(Path.Combine(harness.Packages, keptName), installedBytes);
            harness.Publish("1.0.200");
            var gateway = GatewayOver(harness, state.Store);

            Assert.True((await gateway.CheckAsync(CancellationToken.None)).CanDownload);
            Assert.True((await gateway.DownloadAsync(CancellationToken.None)).CanApply);

            var kept = state.Store.Read().Kept;
            Assert.Equal("1.0.100", kept?.Version);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(installedBytes)), kept?.Sha256);
            Assert.True(File.Exists(Path.Combine(state.Store.KeptFolder, keptName)));
        }

        using (var updated = new RoughChannelHarness(installedVersion: "1.0.200"))
        {
            updated.Publish("1.0.200");
            var gateway = GatewayOver(updated, state.Store);

            var offer = await gateway.FindPreviousAsync(CancellationToken.None);
            Assert.Equal(RollbackSource.KeptCopy, offer.Previous?.Source);
            Assert.Equal("1.0.100", offer.Previous?.Version);

            var fetched = await gateway.DownloadPreviousAsync(offer, null, CancellationToken.None);
            Assert.True(fetched.CanApply, fetched.Status);
            ((IUpdateRollback)gateway).ApplyAndRestart();
            var apply = Assert.Single(updated.Locator.Recorded.Started);
            Assert.Equal(Path.Combine(updated.Packages, keptName), apply.Arguments[apply.Arguments.ToList().IndexOf("--package") + 1]);
            Assert.Equal("kept copy on this PC", state.Store.Read().Applied?.FeedHost);
        }
    }

    [Fact]
    public async Task AKeptCopyThatChangedOnDiskIsRefused()
    {
        using var state = new TemporaryState();
        Directory.CreateDirectory(state.Store.KeptFolder);
        var keptName = $"{Pack}-1.0.100-full.nupkg";
        await File.WriteAllBytesAsync(Path.Combine(state.Store.KeptFolder, keptName), RandomNumberGenerator.GetBytes(1024));
        state.Store.Write(new UpdateState(Kept: new("1.0.100", keptName, new string('E', 64), 1024)));
        using var harness = new RoughChannelHarness(installedVersion: "1.0.200");
        harness.Publish("1.0.200");
        var gateway = GatewayOver(harness, state.Store);

        var offer = await gateway.FindPreviousAsync(CancellationToken.None);
        var fetched = await gateway.DownloadPreviousAsync(offer, null, CancellationToken.None);

        Assert.False(fetched.CanApply);
        Assert.StartsWith("Refused", fetched.Status, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(((IUpdateRollback)gateway).ApplyAndRestart);
        Assert.Empty(harness.Locator.Recorded.Started);
        Assert.Null(state.Store.Read().Pin);
    }

    /// <summary>
    /// #937: a newer build was downloaded ("Update ready · Restart"), then going back was tried and
    /// its fetch failed. The updater empties packages\ when it downloads, so the newer package is
    /// gone; Restart used to return quietly and leave Setup on "Installing…". Now it says so, and
    /// downloading again applies the newer build, not the older one.
    /// </summary>
    [Fact]
    public async Task AFailedFetchOfTheOlderBuildNeverLeavesRestartDoingNothing()
    {
        using var state = new TemporaryState();
        Directory.CreateDirectory(state.Store.KeptFolder);
        var keptName = $"{Pack}-1.0.100-full.nupkg";
        await File.WriteAllBytesAsync(Path.Combine(state.Store.KeptFolder, keptName), RandomNumberGenerator.GetBytes(1024));
        state.Store.Write(new UpdateState(Kept: new("1.0.100", keptName, new string('E', 64), 1024)));
        using var harness = new RoughChannelHarness(installedVersion: "1.0.200");
        harness.Publish("1.0.300");
        var gateway = GatewayOver(harness, state.Store);
        Assert.True((await gateway.CheckAsync(CancellationToken.None)).CanDownload);
        Assert.True((await gateway.DownloadAsync(CancellationToken.None)).CanApply);

        var offer = await gateway.FindPreviousAsync(CancellationToken.None);
        Assert.Equal("1.0.100", offer.Previous?.Version);
        Assert.False((await gateway.DownloadPreviousAsync(offer, null, CancellationToken.None)).CanApply);
        Assert.Empty(Directory.GetFiles(harness.Packages, "*.nupkg"));

        Assert.Throws<UpdateNotDownloadedException>(gateway.ApplyAndRestart);
        Assert.Empty(harness.Locator.Recorded.Started);

        Assert.True((await gateway.DownloadAsync(CancellationToken.None)).CanApply);
        gateway.ApplyAndRestart();
        var apply = Assert.Single(harness.Locator.Recorded.Started);
        var package = apply.Arguments[apply.Arguments.ToList().IndexOf("--package") + 1];
        Assert.Equal($"{Pack}-1.0.300-full.nupkg", Path.GetFileName(package));
        Assert.False(state.Store.Read().Applied?.WentBack);
        Assert.Null(state.Store.Read().Pin);
    }

    /// <summary>#937: an older build fetched and not applied is not what "Update now" installs.</summary>
    [Fact]
    public async Task AFetchedOlderBuildIsNotWhatTheForwardRestartApplies()
    {
        using var state = new TemporaryState();
        using var harness = new RoughChannelHarness(installedVersion: "1.0.200");
        harness.PublishAll("1.0.100", "1.0.200");
        var gateway = GatewayOver(harness, state.Store);

        var offer = await gateway.FindPreviousAsync(CancellationToken.None);
        Assert.True((await gateway.DownloadPreviousAsync(offer, null, CancellationToken.None)).CanApply);

        Assert.Throws<UpdateNotDownloadedException>(gateway.ApplyAndRestart);
        Assert.Empty(harness.Locator.Recorded.Started);
        Assert.Null(state.Store.Read().Pin);
    }

    [Fact]
    public async Task TheButtonAsksFirstAndOnlyTheSecondPressHandsOver()
    {
        var fake = new FakeRollback();
        var model = new SetupRollbackViewModel(fake);
        Assert.Equal(RollbackStage.Idle, model.Stage);

        // A confirmation with nothing asked is nothing.
        await model.ConfirmAsync();
        Assert.Equal(0, fake.Downloads);

        await model.StartAsync();
        Assert.Equal(RollbackStage.Confirming, model.Stage);
        Assert.Equal("Go back from 1.0.200 to 1.0.100?", model.ConfirmHeading);
        Assert.Contains(model.ConfirmSteps, step => step.Contains("until a build newer than 1.0.300", StringComparison.Ordinal));
        Assert.Equal(0, fake.Applies);

        model.Cancel();
        Assert.Equal(RollbackStage.Idle, model.Stage);
        Assert.Null(model.Offer);

        await model.StartAsync();
        await model.ConfirmAsync();
        Assert.Equal(RollbackStage.Applying, model.Stage);
        Assert.Equal(1, fake.Downloads);
        Assert.Equal(1, fake.Applies);
    }

    [Fact]
    public async Task AFailedFetchNeverHandsOverAndNothingToGoBackToSaysWhy()
    {
        var fake = new FakeRollback { DownloadResult = new("Could not fetch 1.0.100 · offline", Failed: true) };
        var model = new SetupRollbackViewModel(fake);
        await model.StartAsync();
        await model.ConfirmAsync();
        Assert.Equal(RollbackStage.Failed, model.Stage);
        Assert.Equal(0, fake.Applies);
        Assert.True(model.CanStart);

        var none = new SetupRollbackViewModel(new FakeRollback { Previous = null });
        await none.StartAsync();
        Assert.Equal(RollbackStage.Nothing, none.Stage);
        Assert.Equal("No older build", none.Status);
        Assert.True(none.ShowsInstallerNote);

        var folder = new SetupRollbackViewModel(new FakeRollback { Installed = false });
        Assert.Equal(RollbackStage.Unavailable, folder.Stage);
        Assert.False(folder.CanStart);
    }

    [Fact]
    public async Task ResumingUpdatesEndsThePinAndChecksAgain()
    {
        var fake = new FakeRollback { CurrentPin = new("1.0.100", "1.0.300", DateTimeOffset.UnixEpoch) };
        var checkedAgain = false;
        var model = new SetupRollbackViewModel(fake, () =>
        {
            checkedAgain = true;
            return Task.CompletedTask;
        });
        Assert.True(model.HasPin);
        Assert.Equal("Staying on 1.0.100 until a build newer than 1.0.300", model.PinText);

        model.ResumeUpdatesCommand.Execute(null);
        await Task.Yield();

        Assert.False(model.HasPin);
        Assert.True(checkedAgain);
    }

    private static VelopackUpdateGateway GatewayOver(RoughChannelHarness harness, IUpdateStateStore store)
    {
        var transport = new DirectoryUpdateFeedTransport(harness.Feed);
        return VelopackUpdateGateway.Create(
            UpdateChannel.Rough,
            new HashVerifiedUpdateSource(transport, harness.Log),
            harness.Locator,
            harness.Log,
            transport,
            store);
    }

    private sealed class TemporaryState : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "tc-update-state-" + Guid.NewGuid().ToString("N"));

        public TemporaryState() => Store = new UpdateStateFile(_folder);

        public UpdateStateFile Store { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_folder, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    private sealed class FakeRollback : IUpdateRollback
    {
        public bool Installed { get; init; } = true;

        public RollbackCandidate? Previous { get; init; } = new("1.0.100", "p.nupkg", new string('A', 64), 10, RollbackSource.Feed);

        public UpdateProgress DownloadResult { get; init; } = new("ready", CanApply: true, Available: "1.0.100");

        public UpdatePin? CurrentPin { get; set; }

        public int Downloads { get; private set; }

        public int Applies { get; private set; }

        public bool IsInstalled => Installed;

        public UpdatePin? Pin => CurrentPin;

        public Task<RollbackOffer> FindPreviousAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new RollbackOffer("1.0.200", Previous, "1.0.300", Previous is null ? "No older build" : null));

        public Task<UpdateProgress> DownloadPreviousAsync(RollbackOffer offer, Action<int>? progress, CancellationToken cancellationToken)
        {
            Downloads++;
            return Task.FromResult(DownloadResult);
        }

        public void ApplyAndRestart() => Applies++;

        public void ResumeUpdates() => CurrentPin = null;

        public IReadOnlyList<UpdateProvenanceRow> Provenance() => [];
    }
}
