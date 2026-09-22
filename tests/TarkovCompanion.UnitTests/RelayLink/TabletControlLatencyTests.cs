using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.UnitTests.RelayDeviceSecurity;
using Xunit.Abstractions;

namespace TarkovCompanion.UnitTests.RelayLink;

/// <summary>
/// #604: how long a tablet's Control gesture takes to reach the desktop, measured end to end with
/// a real tablet page (touch drags in headless Chromium), a real in-process relay, and a real
/// desktop bridge running its own read loop (no test-driven polling, which would hide the loop's
/// own wait).
/// </summary>
/// <remarks>
/// For every touch move the page prints when it happened and the camera it left the tablet on.
/// This records when <see cref="RelayMarksBridge.DesktopWorkspaceRequested"/> hands the desktop
/// each viewport. A move's latency is the time until the desktop was handed that move's camera or
/// a later one in the same drag, which is what a person sees: the desk catching up with the
/// finger. Moves the desktop was never handed within five seconds count as five seconds.
/// </remarks>
[Collection(RelayAdminKeyCollection.Name)]
public sealed class TabletControlLatencyTests(ITestOutputHelper output)
{
    /// <summary>
    /// Loose on purpose: CI runners are shared and slow. The dev measurement is in the PR and in
    /// the report this writes; this bound only has to fail the old two-second poll.
    /// </summary>
    private static readonly TimeSpan P95Bound = TimeSpan.FromMilliseconds(900);

    [Fact]
    public async Task AControlDragReachesTheDesktopWhileTheFingerIsStillMoving()
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
        var pairingCode = desktop.Panel.PairingCode!;

        var handed = new List<(DateTimeOffset At, double X, double Z)>();
        var handedGate = new object();
        desktop.Bridge.DesktopWorkspaceRequested += projection =>
        {
            if (projection.Viewport is { } viewport)
            {
                lock (handedGate)
                {
                    handed.Add((DateTimeOffset.UtcNow, viewport.Center.X, viewport.Center.Z));
                }
            }
        };

        // Only the fake clock is moved here, in step with real time. The desktop's reads are its
        // own loop's, exactly as in the app.
        using var background = new CancellationTokenSource();
        var ticking = Task.Run(async () =>
        {
            while (!background.IsCancellationRequested)
            {
                clock.Advance(TimeSpan.FromMilliseconds(50));
                try
                {
                    await Task.Delay(50, background.Token);
                }
                catch (OperationCanceledException)
                {
                }
            }
        });
        var approving = Task.Run(async () =>
        {
            while (!background.IsCancellationRequested)
            {
                if (desktop.Authority.Snapshot.CanonicalState.DeviceModes.PendingControl is { } pending)
                {
                    try
                    {
                        await desktop.Bridge.ApplyDesktopCommandAsync(
                            new ResolveControlCommand(
                                new CommandId(Guid.NewGuid()),
                                new AggregateRevision(desktop.Authority.Snapshot.CanonicalState.DeviceModes.Cursor.Revision.Value + 1),
                                clock.GetUtcNow(),
                                clock.GetUtcNow().AddMinutes(1),
                                pending.RequestCommandId,
                                approved: true,
                                new ControlLeaseId(Guid.NewGuid())),
                            background.Token);
                    }
                    catch (Exception) when (!background.IsCancellationRequested)
                    {
                    }
                }

                try
                {
                    await Task.Delay(30, background.Token);
                }
                catch (OperationCanceledException)
                {
                }
            }
        });

        var scriptPath = Path.Combine(RepositoryRoot(), "scripts", "test-tablet-control-latency.cjs");
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

            await Task.WhenAny(browserProcess.WaitForExitAsync(), Task.Delay(TimeSpan.FromSeconds(120)));
            var stderr = await stderrTask.WaitAsync(TimeSpan.FromSeconds(10))
                .ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : "<stderr read timed out>", TaskScheduler.Default);
            string[] lines;
            lock (stdoutGate)
            {
                lines = [.. stdoutLog];
            }

            Assert.True(
                browserProcess.HasExited && browserProcess.ExitCode == 0 && lines.Contains("ALL_DONE"),
                $"The browser did not finish.\nstdout:\n{string.Join('\n', lines)}\nstderr:\n{stderr}");

            (DateTimeOffset At, double X, double Z)[] events;
            lock (handedGate)
            {
                events = [.. handed];
            }

            var latencies = Measure(lines, events);
            Assert.NotEmpty(latencies);
            var p50 = Percentile(latencies, 0.50);
            var p95 = Percentile(latencies, 0.95);
            var summary = string.Create(
                CultureInfo.InvariantCulture,
                $"moves={latencies.Count} handed={events.Length} p50={p50.TotalMilliseconds:0}ms p95={p95.TotalMilliseconds:0}ms max={latencies.Max().TotalMilliseconds:0}ms");
            output.WriteLine(summary);
            if (Environment.GetEnvironmentVariable("TARKOV_LATENCY_REPORT") is { Length: > 0 } reportPath)
            {
                await File.AppendAllTextAsync(reportPath, summary + Environment.NewLine);
                if (Environment.GetEnvironmentVariable("TARKOV_LATENCY_DETAIL") == "1")
                {
                    await File.AppendAllLinesAsync(reportPath, events.Select(item => string.Create(
                        CultureInfo.InvariantCulture,
                        $"HANDED {item.At.ToUnixTimeMilliseconds()} {item.X} {item.Z}")));
                    await File.AppendAllLinesAsync(reportPath, lines.Where(line => line.StartsWith("STEP ", StringComparison.Ordinal) || line.StartsWith("DRAG_END", StringComparison.Ordinal)));
                }
            }

            Assert.True(p95 <= P95Bound, summary);
        }
        finally
        {
            background.Cancel();
            await ticking;
            await approving;
            if (!browserProcess.HasExited)
            {
                browserProcess.Kill(entireProcessTree: true);
            }
        }
    }

    /// <summary>One latency per touch move: until the desktop was handed that camera or a later one.</summary>
    private static List<TimeSpan> Measure(string[] lines, (DateTimeOffset At, double X, double Z)[] events)
    {
        var steps = lines
            .Where(line => line.StartsWith("STEP ", StringComparison.Ordinal))
            .Select(line => line.Split(' '))
            .Select(parts => (
                Drag: int.Parse(parts[1], CultureInfo.InvariantCulture),
                Index: int.Parse(parts[2], CultureInfo.InvariantCulture),
                At: DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(parts[3], CultureInfo.InvariantCulture)),
                X: double.Parse(parts[4], CultureInfo.InvariantCulture),
                Y: double.Parse(parts[5], CultureInfo.InvariantCulture)))
            .ToArray();
        var latencies = new List<TimeSpan>();
        foreach (var step in steps)
        {
            // A drag only ever moves the camera one way, so "that move or a later one" is any
            // camera at least as far along as this one. Matching exact cameras is not possible:
            // the browser coalesces touch moves, so the camera read after a move can lag it.
            var drag = steps.Where(item => item.Drag == step.Drag).ToArray();
            var direction = Math.Sign(drag[^1].X - drag[0].X);
            var reached = events
                .Where(item => item.At >= step.At && (item.X - step.X) * direction >= -1e-6)
                .Select(item => item.At - step.At)
                .DefaultIfEmpty(TimeSpan.FromSeconds(5))
                .Min();
            latencies.Add(reached > TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : reached);
        }

        return latencies;
    }

    private static TimeSpan Percentile(List<TimeSpan> values, double fraction)
    {
        var sorted = values.Order().ToArray();
        return sorted[Math.Clamp((int)Math.Ceiling(fraction * sorted.Length) - 1, 0, sorted.Length - 1)];
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
