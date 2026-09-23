using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.UnitTests.RelayDeviceSecurity;

namespace TarkovCompanion.UnitTests.RelayLink;

/// <summary>
/// #708's recovery path in real Chromium: a remembered desktop never traps the tablet on
/// reconnect, and an explicit new QR remains stronger than anything IndexedDB remembers.
/// </summary>
[Collection(RelayAdminKeyCollection.Name)]
public sealed class TabletPairAgainBrowserTests : RealBrowserTestHarness
{
    [RealBrowserFact]
    public async Task AStalePairingCanBeForgottenAndAFreshQrStartsItsNewCode()
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
        var firstCode = Assert.IsType<string>(desktop.Panel.PairingCode);

        var scriptPath = Path.Combine(RepositoryRoot(), "scripts", "test-tablet-pair-again.cjs");
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
        startInfo.ArgumentList.Add(firstCode);
        startInfo.ArgumentList.Add("Repair tablet");
        startInfo.ArgumentList.Add(Environment.GetEnvironmentVariable("TABLET_REPAIR_SCREENSHOT_DIR") ?? "-");

        using var browser = StartBrowser(startInfo);
        var stdout = new List<string>();
        var firstAsked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initialPaired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyForFreshCode = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var freshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = Task.Run(async () =>
        {
            string? line;
            while ((line = await browser.StandardOutput.ReadLineAsync()) is not null)
            {
                lock (stdout)
                {
                    stdout.Add(line);
                }

                if (line == "FIRST_PAIRING_ASKED") firstAsked.TrySetResult();
                if (line == "INITIAL_PAIRED") initialPaired.TrySetResult();
                if (line == "READY_FOR_FRESH_CODE") readyForFreshCode.TrySetResult();
                if (line == "FRESH_CODE_STARTED") freshStarted.TrySetResult();
            }
        });
        var stderrTask = browser.StandardError.ReadToEndAsync();

        try
        {
            await firstAsked.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await UntilAsync(() => desktop.Panel.IsAwaitingApproval || browser.HasExited, "the first pairing request");
            Assert.False(browser.HasExited, Output("The browser exited before approval.", stdout));
            await ((AsyncDelegateCommand)desktop.Panel.ApproveCommand).ExecuteAsync();
            await initialPaired.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await readyForFreshCode.Task.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.True(desktop.Panel.CanStartPairing, desktop.Panel.StatusMessage);
            await ((AsyncDelegateCommand)desktop.Panel.StartPairingCommand).ExecuteAsync();
            var freshQrUrl = Assert.IsType<string>(desktop.Panel.QrPayload);
            await browser.StandardInput.WriteLineAsync(freshQrUrl);
            await browser.StandardInput.FlushAsync();

            await freshStarted.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await UntilAsync(() => desktop.Panel.IsAwaitingApproval || browser.HasExited, "the fresh QR pairing request");
            Assert.False(browser.HasExited, Output("The browser exited before the fresh QR request arrived.", stdout));
            Assert.Equal("Tablet", desktop.Panel.RequestedDisplayName);

            await browser.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            await reader.WaitAsync(TimeSpan.FromSeconds(5));
            var stderr = await stderrTask.WaitAsync(TimeSpan.FromSeconds(5));
            var output = Output(string.Empty, stdout);
            Assert.True(
                browser.ExitCode == 0 && output.Contains("ALL_DONE", StringComparison.Ordinal),
                $"exit code {browser.ExitCode}\nstdout:\n{output}\nstderr:\n{stderr}");
            Assert.Contains("RECONNECTING_HAS_PAIR_AGAIN", output, StringComparison.Ordinal);
            Assert.Contains("PAIR_AGAIN_SHOWS_CODE_ENTRY", output, StringComparison.Ordinal);
        }
        finally
        {
            if (!browser.HasExited)
            {
                browser.Kill(entireProcessTree: true);
            }
        }
    }

    private static string Output(string prefix, List<string> lines)
    {
        lock (lines)
        {
            return prefix + "\n" + string.Join('\n', lines);
        }
    }

    private static async Task UntilAsync(Func<bool> condition, string description)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for " + description + ".");
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
