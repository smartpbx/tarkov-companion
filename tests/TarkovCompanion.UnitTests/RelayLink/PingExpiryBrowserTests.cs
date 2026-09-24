using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.UnitTests.RelayDeviceSecurity;

namespace TarkovCompanion.UnitTests.RelayLink;

/// <summary>
/// Issue 584: "pings also arent dissappearing, at least one i set from my tablet" — the client-side
/// half of the fix, proven with a real headless browser against a real in-process relay and desktop.
/// </summary>
/// <remarks>
/// A ping's placement (tablet or desktop right-click) is covered by
/// <c>JsonFileRaidMarkStoreTests</c>, which proves both reach <c>IRaidMarkStore.AddAsync</c>
/// identically and both get the same <c>MapMarkPolicy.PingLifetime</c>. This test is the other
/// half: given a map surface naming a ping with a near-future <c>expiresUtc</c> — exactly what
/// <c>TabletMapSurfaceBuilder</c> now carries from <c>RaidMark.State.ExpiresUtc</c> — does the
/// tablet page actually drop it on its own once that time passes, with nothing else happening (no
/// new publish, no gesture, no reload)? That is
/// <c>scripts/test-tablet-ping-expiry.cjs</c>'s <c>scheduleNextPingExpiry</c> timer, which this
/// spawns and watches the same way <c>TabletScreenshotHarness</c> does.
/// </remarks>
[Collection(RelayAdminKeyCollection.Name)]
public sealed class PingExpiryBrowserTests : RealBrowserTestHarness
{
    [RealBrowserFact]
    public async Task APingTheSurfaceNamesIsGoneFromTheTabletsMapAfterItsLifetime()
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

        // Published before pairing without the ping, the same order TabletScreenshotHarness uses;
        // the ping goes on in a second revision once the tablet is paired.
        Assert.True(await desktop.Bridge.PublishMapSurfaceAsync(
            TabletMapSurfaceJson.Serialize(Surface(1, pingExpiresUtc: null)),
            artwork: null));

        var scriptPath = Path.Combine(RepositoryRoot(), "scripts", "test-tablet-ping-expiry.cjs");
        Assert.True(File.Exists(scriptPath), $"Missing {scriptPath}.");
        var startInfo = new ProcessStartInfo("node")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add(relay.BrowserOrigin.GetLeftPart(UriPartial.Authority));
        startInfo.ArgumentList.Add(pairingCode);
        startInfo.ArgumentList.Add("Raid tablet");
        using var browserProcess = StartBrowser(startInfo);
        var stderrTask = browserProcess.StandardError.ReadToEndAsync();
        var stdoutLines = new List<string>();
        var stdoutGate = new object();
        var paired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reading = Task.Run(async () =>
        {
            string? line;
            while ((line = await browserProcess.StandardOutput.ReadLineAsync()) is not null)
            {
                lock (stdoutGate)
                {
                    stdoutLines.Add(line);
                }

                if (line == "PAIRED")
                {
                    paired.TrySetResult();
                }
            }

            paired.TrySetResult();
        });

        string Stdout()
        {
            lock (stdoutGate)
            {
                return string.Join('\n', stdoutLines);
            }
        }

        try
        {
            // Approve the pairing exactly as a person watching the desktop panel would. Nothing
            // here needs a poll loop: the script sends no command and only ever reads.
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (!desktop.Panel.IsAwaitingApproval && !browserProcess.HasExited && DateTime.UtcNow < deadline)
            {
                await Task.Delay(25);
            }

            if (browserProcess.HasExited)
            {
                Assert.Fail($"The browser exited before requesting pairing (exit {browserProcess.ExitCode}):\n{Stdout()}\n{await stderrTask}");
            }

            Assert.True(desktop.Panel.IsAwaitingApproval, desktop.Panel.StatusMessage);
            await ((AsyncDelegateCommand)desktop.Panel.ApproveCommand).ExecuteAsync();

            // Real time, not the fake clock: the tablet's own drop timer runs against its browser's
            // Date.now(). Ten seconds out, not the production 45 s, and counted from the moment the
            // tablet says it is paired. It used to be counted from before the browser was even
            // launched, so a busy machine could spend the whole ten seconds starting Chromium and
            // pairing, and the ping had gone before the tablet ever drew it.
            await paired.Task.WaitAsync(TimeSpan.FromSeconds(90));
            if (!Stdout().Contains("PAIRED", StringComparison.Ordinal))
            {
                await browserProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Fail($"The browser exited before it was paired:\n{Stdout()}\n{await stderrTask}");
            }

            var expiresUtc = DateTimeOffset.UtcNow.AddSeconds(10);
            Assert.True(await desktop.Bridge.PublishMapSurfaceAsync(
                TabletMapSurfaceJson.Serialize(Surface(2, expiresUtc)),
                artwork: null));
            await browserProcess.StandardInput.WriteLineAsync("EXPIRES " + expiresUtc.ToString("O"));
            browserProcess.StandardInput.Close();

            await Task.WhenAny(browserProcess.WaitForExitAsync(), Task.Delay(TimeSpan.FromSeconds(60)));
            await reading.WaitAsync(TimeSpan.FromSeconds(10)).ContinueWith(_ => { }, TaskScheduler.Default);
            var stdout = Stdout();
            var stderr = await stderrTask.WaitAsync(TimeSpan.FromSeconds(10))
                .ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : "<stderr read timed out>", TaskScheduler.Default);

            Assert.True(browserProcess.HasExited, $"The headless browser did not finish in time.\nstdout:\n{stdout}\nstderr:\n{stderr}");
            var failLines = stdout.Split('\n').Where(line => line.StartsWith("CHECK:FAIL:", StringComparison.Ordinal));
            Assert.True(
                browserProcess.ExitCode == 0 && stdout.Contains("ALL_DONE", StringComparison.Ordinal),
                $"exit code {browserProcess.ExitCode}\nfailed checks:\n{string.Join('\n', failLines)}\nstdout:\n{stdout}\nstderr:\n{stderr}");
        }
        finally
        {
            if (!browserProcess.HasExited)
            {
                browserProcess.Kill(entireProcessTree: true);
            }
        }
    }

    /// <summary>One ping, "ping-expiring", the sole reason this surface exists, or none yet; no
    /// artwork, because this test never draws a pixel, only reads whether the object is still there.</summary>
    private static TabletMapSurface Surface(long revision, DateTimeOffset? pingExpiresUtc) => new(
        Revision: revision,
        MapId: "customs",
        MapName: "Customs",
        VariantKey: "default",
        TransformVersion: "v1",
        Plan: new TabletMapPlan(0, 0, 1000, 1000),
        Artwork: null,
        Attribution: [],
        FloorIds: ["1F"],
        Layers: [new TabletMapLayer("landmarks", "Landmarks", 0, true)],
        Objects: pingExpiresUtc is { } expires
            ? [new("ping-expiring", "landmarks", "Ping", "UserAuthored", "Ping", null, [500, 500], [], null, false, false, expires)]
            : [],
        View: new TabletMapView("1F", 500, 500, 1, null, null),
        Search: null,
        Message: null,
        PublishedUtc: DateTimeOffset.UtcNow);

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
