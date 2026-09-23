using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.UnitTests.RelayDeviceSecurity;

namespace TarkovCompanion.UnitTests.RelayLink;

/// <summary>
/// The tablet page's own JavaScript, in a real headless browser, against a real in-process relay
/// (#289): pair, reload, and it is still connected without typing anything again.
/// </summary>
/// <remarks>
/// Neither #506 nor #514 exercised the page this way — both syntax-checked it with node only,
/// which cannot catch a WebCrypto call the page makes wrong, an IndexedDB schema mismatch, or a
/// DOM id the script and the markup disagree about. index.html's own WebAuthn-shaped device proof
/// is built from the real page's <c>location.hostname</c>/<c>location.origin</c>
/// (<c>signAssertion</c>), which only a real browser can supply honestly — a fake in a C# stand-in
/// like <see cref="TabletSimulator"/> can just assert whatever the server expects.
///
/// Passes trivially wherever the headless browser this drives is not installed — this repository
/// never installs one; it was found under <c>~/.cache/ms-playwright</c>, cached there by an
/// earlier <c>npx playwright</c> run on the host this ran on, not by anything here.
/// </remarks>
[Collection(RelayAdminKeyCollection.Name)]
public sealed class RelayLinkRealBrowserTests : RealBrowserTestHarness
{
    [RealBrowserFact]
    public async Task PairingSurvivesAReloadInARealBrowser()
    {
        if (!HasHeadlessBrowser())
        {
            return;
        }

        using var certificate = CreateSelfSignedCertificate("localhost");
        var clock = new RelayTestClock(DateTimeOffset.UtcNow);
        // Kestrel refuses dynamic (":0") port binding for a DNS name — only for an explicit IP —
        // so the browser-facing HTTPS listener needs a real, pre-chosen port.
        await using var relay = await LinkRelay.StartAsync(clock, ["http://127.0.0.1:0", $"https://localhost:{FindFreePort()}"], certificate);
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

        var scriptPath = Path.Combine(RepositoryRoot(), "scripts", "test-relay-browser-pairing.cjs");
        Assert.True(File.Exists(scriptPath), $"Missing {scriptPath}.");
        var startInfo = new ProcessStartInfo("node")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(scriptPath);
        // GetLeftPart(Authority), not ToString(): a bare Uri stringifies with a trailing slash,
        // which would make the script's own "${relayOrigin}/tablet" a malformed "//tablet".
        startInfo.ArgumentList.Add(relay.BrowserOrigin.GetLeftPart(UriPartial.Authority));
        startInfo.ArgumentList.Add(pairingCode);
        startInfo.ArgumentList.Add("Raid tablet");
        using var browserProcess = StartBrowser(startInfo);
        try
        {
            var stdoutTask = browserProcess.StandardOutput.ReadToEndAsync();
            var stderrTask = browserProcess.StandardError.ReadToEndAsync();

            // Nothing here signals the browser directly; it only ever talks to the desktop
            // through the relay, exactly like production, so this just waits on the same panel
            // property a human operator would watch.
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!desktop.Panel.IsAwaitingApproval && !browserProcess.HasExited && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            if (browserProcess.HasExited)
            {
                Assert.Fail($"The browser exited before requesting pairing (exit {browserProcess.ExitCode}):\n{await stderrTask}\n{await stdoutTask}");
            }

            Assert.True(desktop.Panel.IsAwaitingApproval, desktop.Panel.StatusMessage);
            Assert.Equal("Raid tablet", desktop.Panel.RequestedDisplayName);
            await ((AsyncDelegateCommand)desktop.Panel.ApproveCommand).ExecuteAsync();

            await Task.WhenAny(browserProcess.WaitForExitAsync(), Task.Delay(TimeSpan.FromSeconds(45)));
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            Assert.True(browserProcess.HasExited, $"The headless browser did not finish in time.\nstdout:\n{stdout}\nstderr:\n{stderr}");
            Assert.True(browserProcess.ExitCode == 0, $"exit code {browserProcess.ExitCode}\nstdout:\n{stdout}\nstderr:\n{stderr}");
            Assert.Contains("PAIRED:Raid tablet", stdout, StringComparison.Ordinal);
            Assert.Contains("STILL_CONNECTED_AFTER_RELOAD:Raid tablet", stdout, StringComparison.Ordinal);
        }
        finally
        {
            // A failed assertion above must not leave a headless Chromium behind: node's own
            // browser.close() never runs the moment an await throws out of the try block.
            if (!browserProcess.HasExited)
            {
                browserProcess.Kill(entireProcessTree: true);
            }
        }
    }

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
    /// <c>npx playwright</c> run already cached one.</summary>
    private static bool HasHeadlessBrowser()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var cache = Path.Combine(home, ".cache", "ms-playwright");
        return Directory.Exists(cache) && Directory.EnumerateDirectories(cache, "chromium*").Any();
    }

    /// <summary>
    /// One-hour, DNS-named ("localhost") server certificate, generated in memory: Kestrel needs
    /// something to present, and neither this test nor the browser it drives needs it to be
    /// trusted by anything but that browser's own <c>--ignore-certificate-errors</c> flag.
    /// </summary>
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
        // Round-tripped through PFX: Kestrel needs the private key attached in the shape that
        // survives an X509Certificate2Collection import, which CreateSelfSigned's result alone
        // does not reliably give on every platform.
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
