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
/// [#553] "the tablet pairing was supposed to be for each user to be able to control their own
/// desktop app." Two desktops in one room on one relay, each pairing its own tablet in a real
/// Chromium against the real tablet page: one by the QR link, one by the typed code, neither
/// claiming anything and neither holding an admin key.
/// </summary>
/// <remarks>
/// What the page shows is checked in <c>scripts/test-two-desktop-tablets.cjs</c>; what each desktop
/// holds is checked here, at the instant the script says it has got somewhere (a marker on its
/// stdout) and before it is told to carry on (a line on its stdin), the way
/// <see cref="TabletTouchGestureTests"/> does it.
/// </remarks>
[Collection(RelayAdminKeyCollection.Name)]
public sealed class TwoDesktopBrowserTests : RealBrowserTestHarness
{
    private const string GroupKeyOfTheSquad = "the-squads-own-group-key";

    [RealBrowserFact]
    public async Task EachTabletSeesAndDrivesOnlyItsOwnDesktop()
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
            certificate,
            GroupKeyOfTheSquad);
        var browserOrigin = relay.BrowserOrigin!.GetLeftPart(UriPartial.Authority);
        using var aliceDisk = new DesktopDisk();
        using var bobDisk = new DesktopDisk { DesktopDeviceId = Guid.Parse("10000000-0000-4000-8000-0000000000bb") };
        await using var alice = await DesktopRun.StartAsync(
            aliceDisk, relay.Origin, clock, protectedStorage: false,
            relyingPartyId: relay.BrowserOrigin.IdnHost, tabletOrigin: browserOrigin, groupKey: GroupKeyOfTheSquad);
        await using var bob = await DesktopRun.StartAsync(
            bobDisk, relay.Origin, clock, protectedStorage: false,
            relyingPartyId: relay.BrowserOrigin.IdnHost, tabletOrigin: browserOrigin, groupKey: GroupKeyOfTheSquad);

        // No claim and no admin key: the panel offers pairing as soon as it is up.
        Assert.True(alice.Panel.IsClaimedByThisDesktop, alice.Panel.RelayClaimMessage);
        Assert.True(bob.Panel.IsClaimedByThisDesktop, bob.Panel.RelayClaimMessage);
        Assert.False(relay.Registry.CanAuthenticate);

        await ((AsyncDelegateCommand)alice.Panel.StartPairingCommand).ExecuteAsync();
        await ((AsyncDelegateCommand)bob.Panel.StartPairingCommand).ExecuteAsync();
        Assert.True(alice.Panel.IsAwaitingTablet, alice.Panel.StatusMessage);
        Assert.True(bob.Panel.IsAwaitingTablet, bob.Panel.StatusMessage);
        var qr = alice.Panel.QrPayload!;
        var qrFragment = qr[(qr.IndexOf('#', StringComparison.Ordinal) + 1)..];

        // One pace for both desktops, and the fake clock kept in step with the real one: a
        // browser's commands carry real wall-clock times, and a desktop refuses one from its future.
        using var running = new CancellationTokenSource();
        var pacing = Task.Run(async () =>
        {
            var watch = Stopwatch.StartNew();
            var accounted = TimeSpan.Zero;
            while (!running.IsCancellationRequested)
            {
                foreach (var desktop in new[] { alice, bob })
                {
                    try
                    {
                        await desktop.Bridge.PollOnceAsync(running.Token);
                    }
                    catch (Exception)
                    {
                        // The relay being down for its restart, or a tick racing a command.
                    }
                }

                // Control is granted on Alice's desktop the moment her tablet asks.
                if (alice.Authority.Snapshot.CanonicalState.DeviceModes.PendingControl is { } pending)
                {
                    try
                    {
                        await alice.Bridge.ApplyDesktopCommandAsync(
                            new ResolveControlCommand(
                                new CommandId(Guid.NewGuid()),
                                new AggregateRevision(alice.Authority.Snapshot.CanonicalState.DeviceModes.Cursor.Revision.Value + 1),
                                clock.GetUtcNow(),
                                clock.GetUtcNow().AddMinutes(1),
                                pending.RequestCommandId,
                                approved: true,
                                new ControlLeaseId(Guid.NewGuid())),
                            running.Token);
                    }
                    catch (Exception)
                    {
                        // A revision race with the poll above; the next tick tries again.
                    }
                }

                var elapsed = TimeSpan.FromMilliseconds(Math.Floor(watch.Elapsed.TotalMilliseconds));
                clock.Advance(elapsed - accounted);
                accounted = elapsed;
                try
                {
                    await Task.Delay(100, running.Token);
                }
                catch (OperationCanceledException)
                {
                }
            }
        });

        var scriptPath = Path.Combine(RepositoryRoot(), "scripts", "test-two-desktop-tablets.cjs");
        Assert.True(File.Exists(scriptPath), $"Missing {scriptPath}.");
        var startInfo = new ProcessStartInfo("node")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add(browserOrigin);
        startInfo.ArgumentList.Add(qrFragment);
        startInfo.ArgumentList.Add(bob.Panel.PairingCode!);
        using var browser = StartBrowser(startInfo);
        var stderrTask = browser.StandardError.ReadToEndAsync();
        var log = new List<string>();
        _ = Task.Run(async () =>
        {
            string? line;
            while ((line = await browser.StandardOutput.ReadLineAsync()) is not null)
            {
                lock (log)
                {
                    log.Add(line);
                }
            }
        });

        try
        {
            // Tablet A opened Alice's QR link. Its request is on Alice's panel and not on Bob's.
            await ReachedAsync(browser, log, "A_ASKED");
            await UntilAsync(() => alice.Panel.IsAwaitingApproval, "tablet A's request on desktop A");
            Assert.False(bob.Panel.IsAwaitingApproval, "Tablet A's request reached desktop B.");
            await ((AsyncDelegateCommand)alice.Panel.ApproveCommand).ExecuteAsync();
            await ContinueAsync(browser);

            // Tablet B typed Bob's code. Alice's panel is idle again and stays that way.
            await ReachedAsync(browser, log, "B_ASKED");
            await UntilAsync(() => bob.Panel.IsAwaitingApproval, "tablet B's request on desktop B");
            Assert.False(alice.Panel.IsAwaitingApproval, "Tablet B's request reached desktop A.");
            await ((AsyncDelegateCommand)bob.Panel.ApproveCommand).ExecuteAsync();
            await ContinueAsync(browser);

            await ReachedAsync(browser, log, "BOTH_PAIRED");
            Assert.Single(alice.Panel.Devices);
            Assert.Equal("Bob's tablet", Assert.Single(bob.Panel.Devices).Device.DisplayName);
            Assert.Equal(3, relay.Desktops.Tenants.Length);
            await PublishAsync(alice, "customs", "Customs", clock);
            await PublishAsync(bob, "woods", "Woods", clock);
            await ContinueAsync(browser);

            // Tablet A holds Control of desktop A. One press: A's viewport moves, B's does not.
            await ReachedAsync(browser, log, "CONTROL_READY");
            Assert.Null(bob.Authority.Snapshot.CanonicalState.DeviceModes.PendingControl);
            var aliceBefore = alice.Authority.Snapshot.CanonicalState.Workspace.Projection.Viewport;
            var bobBefore = bob.Authority.Snapshot.CanonicalState.Workspace.Projection.Viewport;
            var bobRevisionBefore = bob.Authority.Snapshot.CanonicalState.GlobalRevision;
            await ContinueAsync(browser);
            await ReachedAsync(browser, log, "CONTROL_PRESSED");
            Assert.NotEqual(aliceBefore, alice.Authority.Snapshot.CanonicalState.Workspace.Projection.Viewport);
            Assert.Equal(bobBefore, bob.Authority.Snapshot.CanonicalState.Workspace.Projection.Viewport);
            Assert.Equal(bobRevisionBefore, bob.Authority.Snapshot.CanonicalState.GlobalRevision);
            await ContinueAsync(browser);

            // The relay restarts. Its registered desktops are read back from disk; maps are memory.
            await ReachedAsync(browser, log, "RESTART_RELAY");
            await relay.RestartAsync();
            Assert.Equal(3, relay.Desktops.Tenants.Length);
            await UntilAsync(
                () => alice.Bridge.OwnerLink == RelayOwnerLinkState.Verified && bob.Bridge.OwnerLink == RelayOwnerLinkState.Verified,
                "both desktops to be back on the restarted relay");
            await PublishAsync(alice, "interchange", "Interchange", clock);
            await PublishAsync(bob, "reserve", "Reserve", clock);
            Assert.True(alice.Panel.IsIdle && bob.Panel.IsIdle, "Nothing was asked of either player.");
            await ContinueAsync(browser);

            // Alice revokes her tablet; Bob publishes again and his tablet must still follow.
            await ReachedAsync(browser, log, "REVOKE_A");
            var row = Assert.Single(alice.Panel.Devices);
            await ((AsyncDelegateCommand)row.RevokeCommand).ExecuteAsync();
            await ((AsyncDelegateCommand)row.RevokeCommand).ExecuteAsync();
            await PublishAsync(bob, "shoreline", "Shoreline", clock);
            await ContinueAsync(browser);

            await Task.WhenAny(browser.WaitForExitAsync(), Task.Delay(TimeSpan.FromSeconds(90)));
            var stderr = await stderrTask.WaitAsync(TimeSpan.FromSeconds(10))
                .ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : "<stderr read timed out>", TaskScheduler.Default);
            string stdout;
            lock (log)
            {
                stdout = string.Join('\n', log);
            }

            Assert.True(browser.HasExited, $"The headless browser did not finish in time.\nstdout:\n{stdout}\nstderr:\n{stderr}");
            var failed = Regex.Matches(stdout, "^CHECK:FAIL:.*$", RegexOptions.Multiline).Select(match => match.Value);
            Assert.True(
                browser.ExitCode == 0 && stdout.Contains("RESULT:PASS", StringComparison.Ordinal),
                $"exit code {browser.ExitCode}\nfailed checks:\n{string.Join('\n', failed)}\nstdout:\n{stdout}\nstderr:\n{stderr}");
            Assert.Equal(DeviceLifecycleStatus.Active, Assert.Single(bob.Authority.Snapshot.Devices).Status);

            if (Environment.GetEnvironmentVariable("TWO_DESKTOP_PROOF_LOG") is { Length: > 0 } proof)
            {
                await File.WriteAllTextAsync(proof, stdout);
            }
        }
        finally
        {
            running.Cancel();
            await pacing;
            if (!browser.HasExited)
            {
                browser.Kill(entireProcessTree: true);
            }
        }
    }

    private static async Task PublishAsync(DesktopRun desktop, string mapId, string mapName, RelayTestClock clock)
    {
        var surface = new TabletMapSurface(
            Revision: 1,
            MapId: mapId,
            MapName: mapName,
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
            PublishedUtc: clock.GetUtcNow());
        Assert.True(await desktop.Bridge.PublishMapSurfaceAsync(TabletMapSurfaceJson.Serialize(surface), artwork: null));
    }

    private static async Task ReachedAsync(Process browser, List<string> log, string marker)
    {
        bool Seen()
        {
            lock (log)
            {
                return log.Any(line => line == marker);
            }
        }

        await UntilAsync(() => Seen() || browser.HasExited, marker);
        if (!Seen())
        {
            string stdout;
            lock (log)
            {
                stdout = string.Join('\n', log);
            }

            Assert.Fail($"The browser exited (exit {browser.ExitCode}) before {marker}:\n{stdout}");
        }
    }

    private static async Task ContinueAsync(Process browser)
    {
        if (!browser.HasExited)
        {
            await browser.StandardInput.WriteLineAsync("NEXT");
            await browser.StandardInput.FlushAsync();
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

    /// <summary>Never installed by this repository; the same check as <see cref="TabletTouchGestureTests"/>.</summary>
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
