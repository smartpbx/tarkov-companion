using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.UnitTests.RelayDeviceSecurity;

namespace TarkovCompanion.UnitTests.RelayLink;

/// <summary>#407/#271 Control actions through a real browser, relay, authority, and desktop bridge.</summary>
[Collection(RelayAdminKeyCollection.Name)]
public sealed class TabletWorkspaceNavigationBrowserTests
{
    [Fact]
    public async Task ControlCanSwitchTheDesktopAndArmFleaWithAppliedAcknowledgements()
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
        var requestedWorkspace = new TaskCompletionSource<WorkspaceProjection>(TaskCreationOptions.RunContinuationsAsynchronously);
        desktop.Bridge.DesktopWorkspaceRequested += projection => requestedWorkspace.TrySetResult(projection);
        var requestedCapture = new TaskCompletionSource<DesktopCaptureIntentRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        desktop.Bridge.DesktopCaptureIntentRequested += request => requestedCapture.TrySetResult(request);

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

        var scriptPath = Path.Combine(RepositoryRoot(), "scripts", "test-tablet-workspace-navigation.cjs");
        var startInfo = new ProcessStartInfo("node")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add(relay.BrowserOrigin.GetLeftPart(UriPartial.Authority));
        startInfo.ArgumentList.Add(pairingCode);
        startInfo.ArgumentList.Add("Workspace tablet");
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
            await UntilAsync(() => desktop.Panel.IsAwaitingApproval || browserProcess.HasExited, "the pairing request");
            AssertNotExited(browserProcess, stdoutLog);
            await ((AsyncDelegateCommand)desktop.Panel.ApproveCommand).ExecuteAsync();
            Assert.True(await desktop.Bridge.PublishMapSurfaceAsync(
                TabletMapSurfaceJson.Serialize(MinimalSurface(clock.GetUtcNow())),
                artwork: null));

            await UntilAsync(
                () => desktop.Authority.Snapshot.CanonicalState.DeviceModes.PendingControl is not null || browserProcess.HasExited,
                "the control request");
            AssertNotExited(browserProcess, stdoutLog);
            var pending = desktop.Authority.Snapshot.CanonicalState.DeviceModes.PendingControl!;
            var disposition = await desktop.Bridge.ApplyDesktopCommandAsync(new ResolveControlCommand(
                new CommandId(Guid.NewGuid()),
                new AggregateRevision(desktop.Authority.Snapshot.CanonicalState.DeviceModes.Cursor.Revision.Value + 1),
                clock.GetUtcNow(),
                clock.GetUtcNow().AddMinutes(1),
                pending.RequestCommandId,
                approved: true,
                new ControlLeaseId(Guid.NewGuid())));
            Assert.Equal(CommandDisposition.Applied, disposition);

            await Task.WhenAny(browserProcess.WaitForExitAsync(), Task.Delay(TimeSpan.FromSeconds(120)));
            var stderr = await stderrTask.WaitAsync(TimeSpan.FromSeconds(10))
                .ContinueWith(task => task.IsCompletedSuccessfully ? task.Result : "<stderr read timed out>", TaskScheduler.Default);
            string stdout;
            lock (stdoutGate)
            {
                stdout = string.Join('\n', stdoutLog);
            }

            Assert.True(browserProcess.HasExited, $"The headless browser did not finish.\nstdout:\n{stdout}\nstderr:\n{stderr}");
            var failures = Regex.Matches(stdout, "^CHECK:FAIL:.*$", RegexOptions.Multiline).Select(match => match.Value);
            Assert.True(
                browserProcess.ExitCode == 0 && stdout.Contains("ALL_DONE", StringComparison.Ordinal),
                $"exit code {browserProcess.ExitCode}\nfailed checks:\n{string.Join('\n', failures)}\nstdout:\n{stdout}\nstderr:\n{stderr}");

            var projection = await requestedWorkspace.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(WorkspaceKind.Plan, projection.Workspace);
            Assert.Null(projection.Viewport);
            Assert.Equal(WorkspaceKind.Plan, desktop.Authority.Snapshot.CanonicalState.Workspace.Projection.Workspace);
            var capture = await requestedCapture.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(ScanIntent.Flea, capture.Command.Intent);
            Assert.Equal("Workspace tablet", capture.DeviceName);
            Assert.Equal(
                capture.Command.CaptureSessionId,
                desktop.Authority.Snapshot.CanonicalState.CaptureIntent.ActiveIntent!.CaptureSessionId);
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
        var deadline = DateTime.UtcNow.AddSeconds(90); // liveness: the browser starts with the rest of the suite running
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for " + what + ".");
            await Task.Delay(25);
        }
    }

    private static void AssertNotExited(Process process, List<string> stdout)
    {
        if (process.HasExited)
        {
            Assert.Fail($"The browser exited early (exit {process.ExitCode}):\n{string.Join('\n', stdout)}");
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
