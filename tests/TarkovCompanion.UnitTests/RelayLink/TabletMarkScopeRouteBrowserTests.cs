using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.UnitTests.RelayDeviceSecurity;

namespace TarkovCompanion.UnitTests.RelayLink;

/// <summary>#289/#290: scope, lifetime and a route chosen on the real tablet page, landing in the desktop's store.</summary>
[Collection(RelayAdminKeyCollection.Name)]
public sealed class TabletMarkScopeRouteBrowserTests : RealBrowserTestHarness
{
    [RealBrowserFact]
    public async Task TabletChoosesScopeAndLifetimeAndDrawsARoute()
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

        var scriptPath = Path.Combine(RepositoryRoot(), "scripts", "test-tablet-mark-scope-route.cjs");
        var startInfo = new ProcessStartInfo("node")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add(relay.BrowserOrigin.GetLeftPart(UriPartial.Authority));
        startInfo.ArgumentList.Add(pairingCode);
        startInfo.ArgumentList.Add("Route tablet");
        using var browserProcess = StartBrowser(startInfo);
        var stderrTask = browserProcess.StandardError.ReadToEndAsync();

        try
        {
            await UntilAsync(() => desktop.Panel.IsAwaitingApproval || browserProcess.HasExited, "the pairing request");
            Assert.False(browserProcess.HasExited, "The browser exited before pairing was approved.");
            await ((AsyncDelegateCommand)desktop.Panel.ApproveCommand).ExecuteAsync();
            Assert.True(await desktop.Bridge.PublishMapSurfaceAsync(
                TabletMapSurfaceJson.Serialize(MinimalSurface(clock.GetUtcNow())),
                artwork: null));

            await browserProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(55));
            var stdout = await browserProcess.StandardOutput.ReadToEndAsync();
            var stderr = await stderrTask.WaitAsync(TimeSpan.FromSeconds(5));
            var failures = Regex.Matches(stdout, "^CHECK:FAIL:.*$", RegexOptions.Multiline).Select(match => match.Value);
            Assert.True(
                browserProcess.ExitCode == 0 && stdout.Contains("ALL_DONE", StringComparison.Ordinal),
                $"exit code {browserProcess.ExitCode}\nfailed checks:\n{string.Join('\n', failures)}\nstdout:\n{stdout}\nstderr:\n{stderr}");

            // The first mark was placed "Just me" for five minutes and then shared; the route's three
            // stops are Squad waypoints of one route, in order.
            var marks = disk.Marks.Marks;
            Assert.Equal(4, marks.Count);
            var single = Assert.Single(marks, mark => mark.Route is null);
            Assert.Equal(RaidMarkScope.Squad, single.Scope);
            Assert.Equal(RaidMarkLifetime.FiveMinutes, single.Lifetime);
            Assert.NotNull(single.State.ExpiresUtc);
            var stops = marks.Where(mark => mark.Route is not null).OrderBy(mark => mark.Route!.Step).ToArray();
            Assert.Equal([1, 2, 3], stops.Select(mark => mark.Route!.Step));
            Assert.Single(stops.Select(mark => mark.Route!.RouteId).Distinct());
            Assert.All(stops, mark => Assert.Equal(RaidMarkScope.Squad, mark.Scope));
            Assert.All(stops, mark => Assert.Equal(RaidMarkKind.Waypoint, mark.Kind));
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

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for " + what + ".");
            await Task.Delay(25);
        }
    }

    private static bool HasHeadlessBrowser()
    {
        var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "ms-playwright");
        return Directory.Exists(cache) && Directory.EnumerateDirectories(cache, "chromium*").Any();
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

    private static X509Certificate2 CreateSelfSignedCertificate(string hostName)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={hostName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(hostName);
        request.CertificateExtensions.Add(names.Build());
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
