using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.UnitTests.RelayDeviceSecurity;

namespace TarkovCompanion.UnitTests.RelayLink;

/// <summary>
/// #570: "pinch zoom nor the zoom button is working on the tablet side, and im guessing pan isnt
/// either" — driven with real touch input (CDP <c>Input.dispatchTouchEvent</c> for drag/pinch,
/// <c>page.mouse.wheel</c>, a real tap) against the real tablet page, a real in-process relay, and
/// a real desktop coordinator, in each of Follow, Control and Independent.
/// </summary>
/// <remarks>
/// The gesture work and the assertions both live in <c>scripts/test-tablet-touch-gestures.cjs</c>,
/// which only a real browser can run honestly (a hand-rolled pointer-event dispatch from Node
/// cannot produce a trusted <c>pointerType:"touch"</c> the way a real touch-emulated Chromium
/// context does). This class supplies the one thing the script cannot see for itself — whether the
/// *desktop's* canonical workspace actually moved — by watching the script's stdout for markers
/// and answering each with a line on stdin once it has taken its own snapshot, so the two
/// processes are provably looking at the same instant rather than racing each other.
/// </remarks>
[Collection(RelayAdminKeyCollection.Name)]
public sealed class TabletTouchGestureTests : RealBrowserTestHarness
{
    private static readonly string[] ControlSteps =
        ["zoom-in", "zoom-out", "wheel", "fit", "drag", "pinch", "double-tap"];

    [RealBrowserFact]
    public async Task GesturesMoveTheRightViewInEachMode()
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

        // Keeps canonical state flowing to the browser and the fake clock roughly in step with
        // real time, the same reasons TabletScreenshotHarness runs one of these.
        using var pollLoop = new CancellationTokenSource();
        var polling = Task.Run(async () =>
        {
            while (!pollLoop.IsCancellationRequested)
            {
                try
                {
                    await desktop.Bridge.PollOnceAsync(pollLoop.Token);
                }
                catch (OperationCanceledException)
                {
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

        // Grants Control the moment the browser asks, whenever that happens — this test does not
        // otherwise know or care exactly when the script gets there.
        using var autoApprove = new CancellationTokenSource();
        var approving = Task.Run(async () =>
        {
            while (!autoApprove.IsCancellationRequested)
            {
                if (desktop.Authority.Snapshot.CanonicalState.DeviceModes.PendingControl is { } pending)
                {
                    var modesRevision = desktop.Authority.Snapshot.CanonicalState.DeviceModes.Cursor.Revision.Value;
                    try
                    {
                        await desktop.Bridge.ApplyDesktopCommandAsync(
                            new ResolveControlCommand(
                                new CommandId(Guid.NewGuid()),
                                new AggregateRevision(modesRevision + 1),
                                clock.GetUtcNow(),
                                clock.GetUtcNow().AddMinutes(1),
                                pending.RequestCommandId,
                                approved: true,
                                new ControlLeaseId(Guid.NewGuid())),
                            autoApprove.Token);
                    }
                    catch (Exception) when (!autoApprove.IsCancellationRequested)
                    {
                        // A revision race against the poll loop above; the next tick retries
                        // against whatever canonical state looks like by then.
                    }
                }

                try
                {
                    await Task.Delay(30, autoApprove.Token);
                }
                catch (OperationCanceledException)
                {
                }
            }
        });

        var scriptPath = Path.Combine(RepositoryRoot(), "scripts", "test-tablet-touch-gestures.cjs");
        Assert.True(File.Exists(scriptPath), $"Missing {scriptPath}.");
        var startInfo = new ProcessStartInfo("node")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add(relay.BrowserOrigin.GetLeftPart(UriPartial.Authority));
        startInfo.ArgumentList.Add(pairingCode);
        startInfo.ArgumentList.Add("Raid tablet");
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
            // Approve the pairing exactly as a person watching the desktop panel would.
            await UntilAsync(() => desktop.Panel.IsAwaitingApproval || browserProcess.HasExited, "the tablet's pairing request");
            AssertNotExited(browserProcess, stdoutLog);
            Assert.True(desktop.Panel.IsAwaitingApproval, desktop.Panel.StatusMessage);
            await ((AsyncDelegateCommand)desktop.Panel.ApproveCommand).ExecuteAsync();

            var published = await desktop.Bridge.PublishMapSurfaceAsync(
                TabletMapSurfaceJson.Serialize(MinimalSurface(clock.GetUtcNow())),
                artwork: null);
            Assert.True(published);

            // Nothing should have reached the desktop's canonical workspace across the whole of
            // Follow and Independent: one snapshot bracketing both.
            await UntilAsync(() => ContainsLine(stdoutLog, stdoutGate, "LOCAL_MODES_START") || browserProcess.HasExited, "LOCAL_MODES_START");
            AssertNotExited(browserProcess, stdoutLog);
            var beforeLocalModes = desktop.Authority.Snapshot.CanonicalState.Workspace.Projection.Viewport;
            await SendLineAsync(browserProcess, "NEXT");

            await UntilAsync(() => ContainsLine(stdoutLog, stdoutGate, "LOCAL_MODES_END") || browserProcess.HasExited, "LOCAL_MODES_END");
            AssertNotExited(browserProcess, stdoutLog);
            var afterLocalModes = desktop.Authority.Snapshot.CanonicalState.Workspace.Projection.Viewport;
            Assert.Equal(beforeLocalModes, afterLocalModes);
            await SendLineAsync(browserProcess, "NEXT");

            // Every Control gesture, on the other hand, must change it.
            await UntilAsync(() => ContainsLine(stdoutLog, stdoutGate, "CONTROL_START") || browserProcess.HasExited, "CONTROL_START");
            AssertNotExited(browserProcess, stdoutLog);
            var previousViewport = desktop.Authority.Snapshot.CanonicalState.Workspace.Projection.Viewport;
            await SendLineAsync(browserProcess, "NEXT");

            foreach (var step in ControlSteps)
            {
                var marker = $"CONTROL_STEP:{step}";
                await UntilAsync(() => ContainsLine(stdoutLog, stdoutGate, marker) || browserProcess.HasExited, marker);
                AssertNotExited(browserProcess, stdoutLog);
                var current = desktop.Authority.Snapshot.CanonicalState.Workspace.Projection.Viewport;
                Assert.False(
                    Equals(previousViewport, current),
                    $"Control's '{step}' gesture did not change the desktop's canonical viewport (still {current}).");
                previousViewport = current;
                await SendLineAsync(browserProcess, "NEXT");
            }

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
            autoApprove.Cancel();
            await approving;
            if (!pollLoop.IsCancellationRequested)
            {
                pollLoop.Cancel();
            }

            await polling;
            if (!browserProcess.HasExited)
            {
                browserProcess.Kill(entireProcessTree: true);
            }
        }
    }

    private static async Task SendLineAsync(Process process, string line)
    {
        if (process.HasExited)
        {
            return;
        }

        await process.StandardInput.WriteLineAsync(line);
        await process.StandardInput.FlushAsync();
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
