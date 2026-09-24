using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.UnitTests.RelayDeviceSecurity;

namespace TarkovCompanion.UnitTests.RelayLink;

/// <summary>
/// [#846] in real Chromium: a returning tablet its desktop no longer recognises shows the code
/// form within a couple of seconds. The page from before waited a minute on the unanswered ticket.
/// </summary>
[Collection(RelayAdminKeyCollection.Name)]
public sealed class TabletResumeRefusedBrowserTests : RealBrowserTestHarness
{
    [RealBrowserFact]
    public async Task ATabletTheDesktopNoLongerRecognisesGoesToTheCodeStepAtOnce()
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

        var scriptPath = Path.Combine(RepositoryRoot(), "scripts", "test-tablet-resume-refused.cjs");
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
        startInfo.ArgumentList.Add("Raid tablet");

        using var browser = StartBrowser(startInfo);
        var stdout = new List<string>();
        var firstAsked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initialPaired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
            }
        });
        var stderrTask = browser.StandardError.ReadToEndAsync();
        using var stopPolling = new CancellationTokenSource();
        Task? polling = null;

        try
        {
            await firstAsked.Task.WaitAsync(TimeSpan.FromSeconds(60));
            await UntilAsync(() => desktop.Panel.IsAwaitingApproval || browser.HasExited, "the first pairing request");
            Assert.False(browser.HasExited, Output("The browser exited before approval.", stdout));
            await ((AsyncDelegateCommand)desktop.Panel.ApproveCommand).ExecuteAsync();
            await initialPaired.Task.WaitAsync(TimeSpan.FromSeconds(60));

            // Revoked on this desktop only; the relay still has the tablet and lets it knock.
            var tablet = Assert.Single(desktop.Authority.Snapshot.Devices, device => device.Role != TarkovCompanion.CompanionProtocol.DeviceAuthorizationRole.Owner);
            await desktop.Authority.RevokeDeviceAsync(tablet.DeviceId, clock.GetUtcNow(), "test");

            // The desktop's own read loop, as the running app has it, so the ticket is seen.
            polling = Task.Run(async () =>
            {
                while (!stopPolling.IsCancellationRequested)
                {
                    try
                    {
                        await desktop.Bridge.PollOnceAsync(stopPolling.Token);
                        await Task.Delay(200, stopPolling.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            });
            await browser.StandardInput.WriteLineAsync("GO");
            await browser.StandardInput.FlushAsync();

            await browser.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));
            await reader.WaitAsync(TimeSpan.FromSeconds(5));
            var stderr = await stderrTask.WaitAsync(TimeSpan.FromSeconds(5));
            var output = Output(string.Empty, stdout);
            Assert.True(
                browser.ExitCode == 0 && output.Contains("ALL_DONE", StringComparison.Ordinal),
                $"exit code {browser.ExitCode}\nstdout:\n{output}\nstderr:\n{stderr}");
            Assert.Contains("CODE_STEP_SHOWN", output, StringComparison.Ordinal);
            if (Environment.GetEnvironmentVariable("TABLET_RESUME_REFUSED_LOG") is { Length: > 0 } logPath)
            {
                File.WriteAllText(logPath, output); // how long the refusal took, for a repeat run
            }
        }
        catch (TimeoutException)
        {
            browser.Kill(entireProcessTree: true);
            var stderr = await stderrTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Fail(Output($"The script stopped short.\nstderr:\n{stderr}\nstdout:", stdout));
        }
        finally
        {
            stopPolling.Cancel();
            if (polling is not null)
            {
                await polling.WaitAsync(TimeSpan.FromSeconds(10));
            }

            if (!browser.HasExited)
            {
                browser.Kill(entireProcessTree: true);
            }

            await desktop.Panel.ResumesSettled.WaitAsync(TimeSpan.FromSeconds(10));
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
        var deadline = DateTime.UtcNow.AddSeconds(60);
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
