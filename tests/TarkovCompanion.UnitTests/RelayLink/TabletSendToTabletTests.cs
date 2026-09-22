using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.UnitTests.RelayDeviceSecurity;

namespace TarkovCompanion.UnitTests.RelayLink;

/// <summary>
/// #290 "Send to tablet": a real tablet page in headless Chromium, a real in-process relay and a
/// real desktop bridge. An Independent tablet ignores the desk's own moves, takes the desk's view
/// when the desk sends it, stays Independent, and is not pulled back by later publishes.
/// </summary>
[Collection(RelayAdminKeyCollection.Name)]
public sealed class TabletSendToTabletTests
{
    [Fact]
    public async Task SendToTabletMovesAnIndependentTabletToTheDesksViewAndNothingElseDoes()
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
        Assert.True(desktop.Panel.IsClaimedByThisDesktop, desktop.Panel.RelayClaimMessage);
        await ((AsyncDelegateCommand)desktop.Panel.StartPairingCommand).ExecuteAsync();
        Assert.True(desktop.Panel.IsAwaitingTablet, desktop.Panel.StatusMessage);
        var pairingCode = desktop.Panel.PairingCode!;

        // The desktop's reads, and the fake clock kept roughly in step with real time, as in
        // TabletTouchGestureTests. Each read is also where the desktop checks for a due mark.
        using var pollLoop = new CancellationTokenSource();
        var polling = Task.Run(async () =>
        {
            while (!pollLoop.IsCancellationRequested)
            {
                try
                {
                    await desktop.Bridge.PollOnceAsync(pollLoop.Token);
                }
                catch (Exception)
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

        var scriptPath = Path.Combine(RepositoryRoot(), "scripts", "test-tablet-send-to-tablet.cjs");
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
        startInfo.ArgumentList.Add("Raid tablet");
        using var browserProcess = Process.Start(startInfo) ?? throw new InvalidOperationException("node did not start.");
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

            await UntilAsync(() => ContainsLine(stdoutLog, stdoutGate, "INDEPENDENT") || browserProcess.HasExited, "INDEPENDENT");
            AssertNotExited(browserProcess, stdoutLog);
            // The desk moves on its own, with no send.
            Assert.True(await desktop.Bridge.PublishMapSurfaceAsync(
                TabletMapSurfaceJson.Serialize(MinimalSurface(clock.GetUtcNow()) with
                {
                    View = new TabletMapView("1F", 300, 300, 2, null, null),
                }),
                artwork: null));

            await UntilAsync(() => ContainsLine(stdoutLog, stdoutGate, "UNSENT_CHECKED") || browserProcess.HasExited, "UNSENT_CHECKED");
            AssertNotExited(browserProcess, stdoutLog);
            // "Send to tablet", in the shape TabletMapSurfacePublisher.SendToTabletAsync publishes.
            var sent = clock.GetUtcNow();
            Assert.True(await desktop.Bridge.PublishMapSurfaceAsync(
                TabletMapSurfaceJson.Serialize(MinimalSurface(clock.GetUtcNow()) with
                {
                    View = new TabletMapView("1F", 250, 700, 3, null, null),
                    SentToTabletUtc = sent,
                }),
                artwork: null));

            await UntilAsync(() => ContainsLine(stdoutLog, stdoutGate, "SENT_CHECKED") || browserProcess.HasExited, "SENT_CHECKED");
            AssertNotExited(browserProcess, stdoutLog);
            // A later publish still carries the same send; it must not pull the tablet back.
            Assert.True(await desktop.Bridge.PublishMapSurfaceAsync(
                TabletMapSurfaceJson.Serialize(MinimalSurface(clock.GetUtcNow()) with
                {
                    View = new TabletMapView("1F", 260, 690, 4, null, null),
                    SentToTabletUtc = sent,
                }),
                artwork: null));

            await Task.WhenAny(browserProcess.WaitForExitAsync(), Task.Delay(TimeSpan.FromSeconds(40)));
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
