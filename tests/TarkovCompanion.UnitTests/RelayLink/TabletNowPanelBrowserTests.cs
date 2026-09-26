using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using TarkovCompanion.App.Services.V2;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.Now;
using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Loot;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Situations;
using TarkovCompanion.UnitTests.RelayDeviceSecurity;
using TarkovCompanion.UnitTests.V2Now;

namespace TarkovCompanion.UnitTests.RelayLink;

/// <summary>
/// #712 0-11: the desktop's Now panel on the real tablet page, a squad row's Ping reaching the
/// desktop's marks, and the page without a panel as it was. The browser half is
/// <c>scripts/test-tablet-now-panel.cjs</c>; set TABLET_SCREENSHOT_DIR to keep its pictures.
/// </summary>
[Collection(RelayAdminKeyCollection.Name)]
public sealed class TabletNowPanelBrowserTests : RealBrowserTestHarness
{
    [RealBrowserFact]
    public async Task TheNowPanelShowsBesideTheMapAndASquadRowPingReachesTheDesktop()
    {
        if (!HasHeadlessBrowser())
        {
            return;
        }

        using var english = NowPanelStateTests.English();
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

        var scriptPath = Path.Combine(RepositoryRoot(), "scripts", "test-tablet-now-panel.cjs");
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
        startInfo.ArgumentList.Add("Now tablet");
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

            // A. Mid-raid, built by the desk's own projection and builder, at the browser's clock.
            var now = DateTimeOffset.UtcNow;
            var midRaid = InRaid(now, minutesIn: 14, length: 35);
            var panel = TabletNowPanelBuilder.Build(
                NowPanelState.Project(midRaid, now, [new NowExit("ZB-1011", 310, "W", IsOffered: true)]),
                midRaid);
            Assert.True(await desktop.Bridge.PublishMapSurfaceAsync(TabletMapSurfaceJson.Serialize(Surface(now) with { Now = panel }), artwork: null));

            await UntilAsync(() => ContainsLine(stdoutLog, stdoutGate, "PING_DONE") || browserProcess.HasExited, "PING_DONE");
            AssertNotExited(browserProcess, stdoutLog);
            await UntilAsync(() => disk.Marks.Marks.Any(mark => mark.State.Label == "Riley"), "Riley's ping in the desktop's store");
            var ping = disk.Marks.Marks.Single(mark => mark.State.Label == "Riley");
            Assert.Equal(TarkovCompanion.Application.Services.Maps.RaidMarkKind.Ping, ping.Kind);
            Assert.Equal((620.0, 180.0), (ping.State.X, ping.State.Y));

            // B. Late raid with a fresh verdict.
            now = DateTimeOffset.UtcNow;
            var late = InRaid(now, minutesIn: 27, length: 35);
            using (LocalTime.UseZone(TimeZoneInfo.Utc))
            {
                var verdict = new NowLootVerdict(
                    2,
                    1,
                    1,
                    0,
                    [
                        new(LootScanVerdict.Take, "Virtex programmable processor", "Gunsmith later", "₽86k / sq"),
                        new(LootScanVerdict.Take, "Fuel conditioner", "Hideout", "₽61k / sq"),
                        new(LootScanVerdict.Swap, "Electric drill", "Swap the wires", "₽32k / sq"),
                    ],
                    now.AddSeconds(-5));
                var latePanel = TabletNowPanelBuilder.Build(
                    NowPanelState.Project(late, now, [new NowExit("ZB-1011", 310, "W", IsOffered: true)], verdict, "The game started the raid."),
                    late);
                Assert.True(await desktop.Bridge.PublishMapSurfaceAsync(TabletMapSurfaceJson.Serialize(Surface(now) with { Now = latePanel }), artwork: null));
            }

            // D. Its flag off, or an older desktop: no panel.
            await UntilAsync(() => ContainsLine(stdoutLog, stdoutGate, "B_DONE") || browserProcess.HasExited, "B_DONE");
            AssertNotExited(browserProcess, stdoutLog);
            Assert.True(await desktop.Bridge.PublishMapSurfaceAsync(TabletMapSurfaceJson.Serialize(Surface(DateTimeOffset.UtcNow)), artwork: null));

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

    /// <summary>The concept's squad: two placed on this map, one on another, one gone quiet.</summary>
    private static Situation InRaid(DateTimeOffset now, int minutesIn, int length)
    {
        var started = now.AddMinutes(-minutesIn);
        return new Situation(7, now, new(SituationPhase.InRaid, new Confidence(0.95), SituationSource.GameLog, started, "The game started the raid."))
        {
            Map = new("customs", Confidence.Certain, SituationSource.GameLog, started, "log"),
            Clock = new(SituationClockBasis.Counted, TimeSpan.FromMinutes(length), started, started, "counted"),
            You = new("Dorms 3-story", "2F", "NE", 45, new WorldPosition(123.4, 2, -56.7), now.AddSeconds(-12), "shot"),
            Squad =
            [
                new("Geo", SquadMemberState.InRaid, "customs", "Old Gas Station", 140, "NE", now.AddSeconds(-12), "shared"),
                new("Riley", SquadMemberState.InRaid, "customs", "Crackhouse", 60, "N", now.AddSeconds(-50), "shared"),
                new("Sam", SquadMemberState.OnAnotherMap, "woods", null, null, null, now.AddSeconds(-40), "shared"),
                new("Kai", SquadMemberState.Quiet, "customs", "Fortress", 220, "S", now.AddMinutes(-4), "shared"),
            ],
            Next = new("zibbo", "Golden Zibbo lighter", 40, "customs", started, "route"),
            Then = new("watch", "bronze pocket watch · Big Red", 220, "customs", started, "route"),
        };
    }

    private static TabletMapSurface Surface(DateTimeOffset now) => new(
        Revision: 1,
        MapId: "customs",
        MapName: "Customs",
        VariantKey: "default",
        TransformVersion: "v1",
        Plan: new TabletMapPlan(0, 0, 1000, 1000),
        Artwork: null,
        Attribution: [],
        FloorIds: ["1F"],
        Layers: [new TabletMapLayer("squad", "Squad", 50, true)],
        Objects:
        [
            new TabletMapObject("squad:Geo", "squad", "TeammateLastKnown", "TeamSharedLastKnown", "Geo", "12 s", [180, 760], [], 90, false),
            new TabletMapObject("squad:Riley", "squad", "TeammateLastKnown", "TeamSharedLastKnown", "Riley", "50 s", [620, 180], [], 200, false),
        ],
        View: new TabletMapView("1F", 500, 500, 1, null, null),
        Search: null,
        Message: null,
        PublishedUtc: now);

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
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for " + what + ".");
            await Task.Delay(25);
        }
    }

    private static int FindFreePort()
    {
        using var socket = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Stream,
            System.Net.Sockets.ProtocolType.Tcp);
        socket.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        return ((System.Net.IPEndPoint)socket.LocalEndPoint!).Port;
    }

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
