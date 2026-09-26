using TarkovCompanion.App.Services.Updates;
using Velopack;
using Velopack.Locators;

namespace TarkovCompanion.UnitTests.Updates;

public sealed class PendingUpdateTests
{
    /// <summary>The owner's log, shortened: four failures, and the reason is on the last error line.</summary>
    private const string Log = """
        [update:4112] [23:55:01] [INFO] Waiting for process pid-29192 to exit.
        [update:4112] [23:55:01] [WARN] Failed to wait for process (29192) to exit (Access is denied. (os error -2147024891)). Continuing...
        [update:4112] [23:55:14] [ERROR] Apply error: An earlier failure that is not the latest.
        [update:5200] [23:56:40] [WARN] Retrying operation in 1000ms... (Os code: 32)
        [update:5200] [23:56:50] [ERROR] Apply error: Unable to start the update, because one or more running processes prevented it.
        [update:5200] [23:56:51] [INFO] Launching app is out-dated. Current: 2.0.1324, Newest Local Available: 2.0.1337
        """;

    [Fact]
    public void TheReasonIsTheLastApplyErrorInTheUpdatersLog() =>
        Assert.Equal(
            "Unable to start the update, because one or more running processes prevented it.",
            VelopackApplyLog.LastApplyError(Log));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[INFO] Applied 2.0.1303 successfully.")]
    [InlineData("[ERROR] Apply error:   ")]
    public void ALogWithNoApplyErrorGivesNoReason(string? log) =>
        Assert.Null(VelopackApplyLog.LastApplyError(log));

    [Fact]
    public void AReasonThatRunsOnIsCutSoTheLineStaysALine()
    {
        var reason = VelopackApplyLog.LastApplyError("[ERROR] Apply error: " + new string('x', 900));

        Assert.NotNull(reason);
        Assert.InRange(reason.Length, 1, 201);
        Assert.EndsWith("…", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ALogThatCannotBeReadGivesNoReasonRatherThanAnError()
    {
        Assert.Null(VelopackApplyLog.ReadLastApplyError(null));
        Assert.Null(VelopackApplyLog.ReadLastApplyError(Path.Combine(Path.GetTempPath(), "tc-no-such-" + Guid.NewGuid().ToString("N"), "velopack.log")));
    }

    [Fact]
    public void TheLogIsReadFromItsTailWhileTheUpdaterStillHasItOpen()
    {
        var path = Path.Combine(Path.GetTempPath(), "tc-velopack-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            using (var writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite))
            {
                writer.Write(System.Text.Encoding.UTF8.GetBytes(new string('.', 300 * 1024) + "\n" + Log));
                writer.Flush();
                Assert.Equal(
                    "Unable to start the update, because one or more running processes prevented it.",
                    VelopackApplyLog.ReadLastApplyError(path));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ThePlayerIsToldWhichBuildDidNotApplyAndWhy()
    {
        Assert.Equal(
            "2.0.1337 is downloaded but the last attempt did not apply",
            new PendingUpdate("2.0.1337", null).Status);
        Assert.Equal(
            "2.0.1337 is downloaded but the last attempt did not apply · one or more running processes prevented it.",
            new PendingUpdate("2.0.1337", "one or more running processes prevented it.").Status);
        Assert.DoesNotContain("game", PendingUpdateText.Advice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ABuildAlreadyDownloadedAtStartupIsReportedAndCanBeAppliedAgain()
    {
        using var harness = new RoughChannelHarness(installedVersion: "1.0.100");
        var locator = new WaitingLocator(harness, "1.0.200");
        var gateway = VelopackUpdateGateway.Create(
            UpdateChannel.Rough,
            new HashVerifiedUpdateSource(new DirectoryUpdateFeedTransport(harness.Feed), harness.Log),
            locator,
            harness.Log);
        string? askedFor = null;

        var pending = gateway.PendingFromLastAttempt(appId =>
        {
            askedFor = appId;
            return "one or more running processes prevented it.";
        });

        Assert.NotNull(pending);
        Assert.Equal("1.0.200", pending.Version);
        Assert.Equal(RoughChannelHarness.PackId, askedFor);
        Assert.Contains("did not apply · one or more running processes", pending.Status, StringComparison.Ordinal);

        // "Apply now" with no check and no download in this run: exactly that package.
        gateway.ApplyAndRestart();
        var apply = Assert.Single(locator.Recorded.Started);
        Assert.Equal(
            Path.Combine(harness.Packages, locator.FileName),
            apply.Arguments[apply.Arguments.ToList().IndexOf("--package") + 1]);
    }

    [Fact]
    public void WithNothingDownloadedNothingIsReported()
    {
        using var harness = new RoughChannelHarness(installedVersion: "1.0.100");
        var gateway = VelopackUpdateGateway.Create(
            UpdateChannel.Rough,
            new HashVerifiedUpdateSource(new DirectoryUpdateFeedTransport(harness.Feed), harness.Log),
            harness.Locator,
            harness.Log);

        var logRead = false;
        Assert.Null(gateway.PendingFromLastAttempt(_ =>
        {
            logRead = true;
            return null;
        }));
        Assert.False(logRead, "no log is read for a build that is not waiting");
        Assert.Throws<UpdateNotDownloadedException>(gateway.ApplyAndRestart);
        Assert.Empty(harness.Locator.Recorded.Started);
    }

    /// <summary>An installation whose packages folder holds a newer full package than the one running.</summary>
    private sealed class WaitingLocator : TestVelopackLocator
    {
        public WaitingLocator(RoughChannelHarness harness, string waiting)
            : base(
                RoughChannelHarness.PackId,
                "1.0.100",
                harness.Packages,
                Path.Combine(harness.InstallRoot, "current"),
                harness.InstallRoot,
                Path.Combine(harness.InstallRoot, "Update.exe"),
                channel: "win")
        {
            FileName = $"{RoughChannelHarness.PackId}-{waiting}-full.nupkg";
            File.WriteAllText(Path.Combine(harness.Packages, FileName), "a package that was downloaded and never applied");
            _waiting = new VelopackAsset
            {
                PackageId = RoughChannelHarness.PackId,
                Version = SemanticVersion.Parse(waiting),
                Type = VelopackAssetType.Full,
                FileName = FileName,
            };
        }

        private readonly VelopackAsset _waiting;

        public string FileName { get; }

        public RoughChannelHarness.RecordedProcess Recorded { get; } = new();

        public override IProcessImpl Process => Recorded;

        public override VelopackAsset? GetLatestLocalFullPackage() => _waiting;
    }
}
