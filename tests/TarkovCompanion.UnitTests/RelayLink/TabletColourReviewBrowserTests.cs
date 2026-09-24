using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using TarkovCompanion.App.Services.V2;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.StashScan;
using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.Core.Domain.Stash;
using TarkovCompanion.UnitTests.RelayDeviceSecurity;

namespace TarkovCompanion.UnitTests.RelayLink;

/// <summary>
/// #290: mark colours chosen on the real tablet page reaching the desktop's store, and the
/// desktop's Stash scan and flea screen shown on the tablet as review cards. The browser half is
/// <c>scripts/test-tablet-colour-review.cjs</c>; set TABLET_SCREENSHOT_DIR to keep its pictures.
/// </summary>
[Collection(RelayAdminKeyCollection.Name)]
public sealed class TabletColourReviewBrowserTests : RealBrowserTestHarness
{
    [RealBrowserFact]
    public async Task ColoursReachTheDesktopAndReviewsOpenAsCards()
    {
        if (!HasHeadlessBrowser())
        {
            return;
        }

        using var certificate = CreateSelfSignedCertificate("localhost");
        var clock = new RelayTestClock(DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        await using var relay = await LinkRelay.StartAsync(
            clock,
            ["http://127.0.0.1:0", $"https://localhost:{FindFreePort()}"],
            certificate);
        Assert.NotNull(relay.BrowserOrigin);
        using var disk = new DesktopDisk();
        await using var desktop = await DesktopRun.StartAsync(
            disk,
            relay.Origin,
            clock,
            protectedStorage: false,
            relyingPartyId: relay.BrowserOrigin!.IdnHost,
            tabletOrigin: relay.BrowserOrigin.GetLeftPart(UriPartial.Authority));

        await desktop.ClaimAsync();
        await ((AsyncDelegateCommand)desktop.Panel.StartPairingCommand).ExecuteAsync();
        var pairingCode = Assert.IsType<string>(desktop.Panel.PairingCode);

        using var pollLoop = new CancellationTokenSource();
        var polling = Task.Run(async () =>
        {
            while (!pollLoop.IsCancellationRequested)
            {
                try
                {
                    await desktop.Bridge.PollOnceAsync(pollLoop.Token);
                }
                catch (Exception) when (!pollLoop.IsCancellationRequested)
                {
                }

                clock.Advance(TimeSpan.FromMilliseconds(150));
                try
                {
                    await Task.Delay(150, pollLoop.Token);
                }
                catch (OperationCanceledException)
                {
                }
            }
        });

        var scriptPath = Path.Combine(RepositoryRoot(), "scripts", "test-tablet-colour-review.cjs");
        Assert.True(File.Exists(scriptPath), $"Missing {scriptPath}.");
        var startInfo = new ProcessStartInfo("node")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add(relay.BrowserOrigin.GetLeftPart(UriPartial.Authority));
        startInfo.ArgumentList.Add(pairingCode);
        startInfo.ArgumentList.Add("Colour tablet");
        using var browserProcess = StartBrowser(startInfo);
        var stderrTask = browserProcess.StandardError.ReadToEndAsync();
        var stdoutLog = new List<string>();
        var stdoutGate = new object();
        _ = Task.Run(async () =>
        {
            string? line;
            while ((line = await browserProcess.StandardOutput.ReadLineAsync()) is not null)
            {
                lock (stdoutGate)
                {
                    stdoutLog.Add(line);
                }
            }
        });

        try
        {
            await UntilAsync(() => desktop.Panel.IsAwaitingApproval || browserProcess.HasExited, "the tablet's pairing request");
            AssertNotExited(browserProcess, stdoutLog);
            await ((AsyncDelegateCommand)desktop.Panel.ApproveCommand).ExecuteAsync();
            Assert.True(await desktop.Bridge.PublishMapSurfaceAsync(
                TabletMapSurfaceJson.Serialize(MinimalSurface(clock.GetUtcNow())),
                artwork: null));

            await UntilAsync(() => ContainsLine(stdoutLog, stdoutGate, "MARKS_DONE") || browserProcess.HasExited, "MARKS_DONE");
            AssertNotExited(browserProcess, stdoutLog);
            await UntilAsync(() => disk.Marks.Marks.Count == 2, "both marks in the desktop's store");

            // The colours the tablet chose are the desktop's marks' colours.
            var ping = Assert.Single(disk.Marks.Marks, mark => mark.Kind == TarkovCompanion.Application.Services.Maps.RaidMarkKind.Ping);
            var waypoint = Assert.Single(disk.Marks.Marks, mark => mark.Kind == TarkovCompanion.Application.Services.Maps.RaidMarkKind.Waypoint);
            Assert.Equal("#E69F00", ping.Colour);
            Assert.Equal("#CC79A7", waypoint.Colour);

            // What the desktop then publishes: those marks drawn in their colours, and both reviews.
            var now = clock.GetUtcNow();
            var surface = MinimalSurface(now) with
            {
                Objects =
                [
                    new TabletMapObject($"mark:{ping.Id}", "marks", "Ping", "UserAuthored", "Ping", null, [ping.State.X, ping.State.Y], [], null, false, false, now.AddMinutes(10), ping.Colour),
                    new TabletMapObject($"mark:{waypoint.Id}", "marks", "Waypoint", "UserAuthored", "1", null, [waypoint.State.X, waypoint.State.Y], [], null, false, false, null, waypoint.Colour),
                ],
                Layers = [new TabletMapLayer("marks", "My marks", 40, true)],
                Stash = StashReview(now.AddMinutes(-20)),
                Flea = FleaReview(now.AddMinutes(-2)),
            };
            Assert.True(await desktop.Bridge.PublishMapSurfaceAsync(TabletMapSurfaceJson.Serialize(surface), artwork: null));

            await Task.WhenAny(browserProcess.WaitForExitAsync(), Task.Delay(TimeSpan.FromSeconds(60)));
            var stderr = await stderrTask.WaitAsync(TimeSpan.FromSeconds(10))
                .ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : "<stderr read timed out>", TaskScheduler.Default);
            string stdout;
            lock (stdoutGate)
            {
                stdout = string.Join('\n', stdoutLog);
            }

            Assert.True(browserProcess.HasExited, $"The headless browser did not finish in time.\nstdout:\n{stdout}\nstderr:\n{stderr}");
            var failLines = Regex.Matches(stdout, "^CHECK:FAIL:.*$", RegexOptions.Multiline).Select(m => m.Value);
            Assert.True(
                browserProcess.ExitCode == 0 && stdout.Contains("ALL_DONE", StringComparison.Ordinal),
                $"exit code {browserProcess.ExitCode}\nfailed checks:\n{string.Join('\n', failLines)}\nstdout:\n{stdout}\nstderr:\n{stderr}");
        }
        finally
        {
            pollLoop.Cancel();
            await polling;
            if (!browserProcess.HasExited)
            {
                browserProcess.Kill(entireProcessTree: true);
            }
        }
    }

    /// <summary>A Stash scan as the Stash page's rows would hand it over, through the real builder.</summary>
    private static TabletCaptureReview StashReview(DateTimeOffset recordedUtc) => TabletCaptureReviewBuilder.FromStash(
        Guid.Parse("6f3c9a51-0000-4000-8000-000000000001"),
        recordedUtc,
        "212 named · 3 unknown",
        "₽18.6M",
        [
            new StashPlanTileViewModel(StashPlanGroup.Keep, "Keep", 1, true),
            new StashPlanTileViewModel(StashPlanGroup.Sell, "Sell", 1, true),
            new StashPlanTileViewModel(StashPlanGroup.Review, "Review", 1, true),
        ],
        [
            new StashItemRowViewModel("k1", "Graphics card", "stash", "x2", "GameWrittenScreenshot · 93%", StashPlanGroup.Keep) { WhyLabel = "Needed for the hideout." },
            new StashItemRowViewModel("k2", "Bolts", "stash", "x5", "GameWrittenScreenshot · 88%", StashPlanGroup.Sell) { WhyLabel = "Nothing needs it." },
            new StashItemRowViewModel("k3", "Unknown item", "stash", "x1", "GameWrittenScreenshot · 41%", StashPlanGroup.Review) { WhyLabel = "Not sure what this is." },
        ]);

    private static TabletCaptureReview FleaReview(DateTimeOffset seenUtc) => TabletCaptureReview.Bounded(
        TabletCaptureReviewBuilder.FleaKind,
        "artifact-flea-1",
        seenUtc,
        "Offers for Graphics card",
        "2 rows read · 1 would pay to resell",
        "Therapist pays ₽120,000 · 24 h average ₽337,352",
        [
            new TabletReviewRow("#1 best buy · ₽290,000 each", "Good buy", "Good", "1 unit", "read 97% sure", "₽31,000 under the 24 h average after the fee."),
            new TabletReviewRow("#2 · ₽350,000 each", "Over average", "Bad", "2 units · ₽700,000 for the lot", "read 91% sure", "Over the 24 h average."),
        ]);

    private static bool ContainsLine(List<string> log, object gate, string marker)
    {
        lock (gate)
        {
            return log.Any(line => line.Contains(marker, StringComparison.Ordinal));
        }
    }

    private static void AssertNotExited(Process process, List<string> stdoutLog)
    {
        if (process.HasExited)
        {
            Assert.Fail($"The browser exited early (exit {process.ExitCode}):\n{string.Join('\n', stdoutLog)}");
        }
    }

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for " + what + ".");
            await Task.Delay(25);
        }
    }

    /// <summary>
    /// A bare map plan wide enough that a drag or a pinch has real room to move in, with no
    /// artwork (this test never draws a pixel, only reads the camera the gestures leave behind) and
    /// no objects (nothing here is testing hit-testing).
    /// </summary>
    private static TabletMapSurface MinimalSurface(DateTimeOffset now) => new(
        Revision: 1,
        MapId: "customs",
        MapName: "Customs",
        VariantKey: "default",
        TransformVersion: "v1",
        Plan: new TabletMapPlan(0, 0, 1000, 1000),
        Artwork: null,
        Attribution: [],
        FloorIds: ["1F"],
        Layers: [new TabletMapLayer("landmarks", "Landmarks", 0, true)],
        Objects: [],
        View: new TabletMapView("1F", 500, 500, 1, null, null),
        Search: null,
        Message: null,
        PublishedUtc: now);

    /// <summary>A momentarily-free loopback port, for the one binding Kestrel cannot pick itself.</summary>
    private static int FindFreePort()
    {
        using var socket = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Stream,
            System.Net.Sockets.ProtocolType.Tcp);
        socket.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        return ((System.Net.IPEndPoint)socket.LocalEndPoint!).Port;
    }

    /// <summary>Never installed by this repository; only ever found where an earlier, unrelated
    /// <c>npx playwright</c> run already cached one. Same check as RelayLinkRealBrowserTests.</summary>
    private static bool HasHeadlessBrowser()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var cache = Path.Combine(home, ".cache", "ms-playwright");
        return Directory.Exists(cache) && Directory.EnumerateDirectories(cache, "chromium*").Any();
    }

    private static X509Certificate2 CreateSelfSignedCertificate(string hostName)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={hostName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
        subjectAlternativeNames.AddDnsName(hostName);
        request.CertificateExtensions.Add(subjectAlternativeNames.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), password: null);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TarkovCompanion.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root was not found above the test output.");
    }
}
