using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using TarkovCompanion.App.Services.V2;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.UnitTests.RelayDeviceSecurity;
using Xunit.Abstractions;

namespace TarkovCompanion.UnitTests.RelayLink;

/// <summary>
/// #925: a map picked on the tablet moved the desktop, and the tablet stayed on the old map. The
/// desktop posts a new map's surface before its picture; the relay woke the tablet on the surface,
/// the tablet fetched the picture the relay still held (the old map's) and filed it under the new
/// map's hash, so it never fetched again. A real tablet page in headless Chromium, a real
/// in-process relay and desktop bridge, and a desk whose picture arrives a moment after its
/// surface, as a real desktop uploading megabytes of plan does.
/// </summary>
[Collection(RelayAdminKeyCollection.Name)]
public sealed class TabletMapArtworkSwitchBrowserTests(ITestOutputHelper output) : RealBrowserTestHarness
{
    private static readonly TabletMapChoice[] DeskMaps =
    [
        new("customs", "Customs"),
        new("shoreline", "Shoreline"),
        new("woods", "Woods"),
    ];

    /// <summary>How long the picture trails its surface: a plan upload on a home connection.</summary>
    private static readonly TimeSpan PictureLag = TimeSpan.FromMilliseconds(400);

    [RealBrowserFact]
    public async Task EveryModeDrawsTheNewMapsPictureAfterASwitch()
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

        var desk = new Desk();
        var applier = new TabletRemoteWorkspaceApplier(desk);
        var applying = new BlockingCollection<WorkspaceProjection>();
        desktop.Bridge.DesktopWorkspaceRequested += projection =>
        {
            if (projection.Workspace == WorkspaceKind.Raid)
            {
                applying.Add(projection);
            }
        };

        using var background = new CancellationTokenSource();
        // One thread applies the tablet's commands in order, as the desktop's UI thread does.
        var applyingThread = Task.Run(async () =>
        {
            try
            {
                foreach (var projection in applying.GetConsumingEnumerable(background.Token))
                {
                    await applier.SubmitAsync(projection);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
        var ticking = Task.Run(async () =>
        {
            while (!background.IsCancellationRequested)
            {
                clock.Advance(TimeSpan.FromMilliseconds(50));
                await Delay(50, background.Token);
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

                await Delay(30, background.Token);
            }
        });

        // A new map goes out as its surface first and its picture after PictureLag: the window
        // in which the relay holds the new map's surface and the old map's picture.
        var publishing = Task.Run(async () =>
        {
            long published = -1;
            string? publishedMap = null;
            while (!background.IsCancellationRequested)
            {
                var (version, surface, artwork) = desk.Snapshot(clock.GetUtcNow());
                if (version != published)
                {
                    try
                    {
                        var json = TabletMapSurfaceJson.Serialize(surface);
                        if (publishedMap is not null && publishedMap != surface.MapId)
                        {
                            await desktop.Bridge.PublishMapSurfaceAsync(json, artwork: null);
                            await Delay((int)PictureLag.TotalMilliseconds, background.Token);
                        }

                        if (await desktop.Bridge.PublishMapSurfaceAsync(json, artwork))
                        {
                            published = version;
                            publishedMap = surface.MapId;
                        }
                    }
                    catch (Exception) when (!background.IsCancellationRequested)
                    {
                    }
                }

                await Delay(20, background.Token);
            }
        });

        var scriptPath = Path.Combine(RepositoryRoot(), "scripts", "test-tablet-map-artwork-switch.cjs");
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
        startInfo.ArgumentList.Add(string.Join(',', DeskMaps.Select(map => map.Id)));
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

                // Follow and Independent cannot pick a map; the desk does, as its player would.
                if (line.StartsWith("WANT_DESK ", StringComparison.Ordinal))
                {
                    _ = desk.SelectMapAsync(line["WANT_DESK ".Length..]);
                }
            }
        });

        try
        {
            await UntilAsync(() => desktop.Panel.IsAwaitingApproval || browserProcess.HasExited, "the tablet's pairing request");
            Assert.False(browserProcess.HasExited, "The browser exited before pairing.");
            await ((AsyncDelegateCommand)desktop.Panel.ApproveCommand).ExecuteAsync();

            await Task.WhenAny(browserProcess.WaitForExitAsync(), Task.Delay(TimeSpan.FromSeconds(120)));
            var stderr = await stderrTask.WaitAsync(TimeSpan.FromSeconds(5))
                .ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : "<stderr read timed out>", TaskScheduler.Default);
            string[] lines;
            lock (stdoutGate)
            {
                lines = [.. stdoutLog];
            }

            var stdout = string.Join('\n', lines);
            Assert.True(browserProcess.HasExited, $"The headless browser did not finish in time.\nstdout:\n{stdout}\nstderr:\n{stderr}");
            var failLines = Regex.Matches(stdout, "^CHECK:FAIL:.*$", RegexOptions.Multiline).Select(m => m.Value).ToArray();
            Assert.True(
                browserProcess.ExitCode == 0 && lines.Contains("ALL_DONE") && failLines.Length == 0,
                $"exit code {browserProcess.ExitCode}\nfailed checks ({failLines.Length}):\n{string.Join('\n', failLines)}\n" +
                $"stdout:\n{stdout}\nstderr:\n{stderr}");
            // Every mode switched: three picked on the tablet in Control, one each by the desk.
            Assert.Equal(3, lines.Count(line => line.StartsWith("SWITCH Control ", StringComparison.Ordinal)));
            Assert.Single(lines, line => line.StartsWith("SWITCH Follow ", StringComparison.Ordinal));
            Assert.Single(lines, line => line.StartsWith("SWITCH Independent ", StringComparison.Ordinal));
            // How long each took, from the pick (or the desk's switch) to the new picture drawn.
            foreach (var line in lines.Where(line => line.StartsWith("SWITCH ", StringComparison.Ordinal)))
            {
                output.WriteLine(line);
            }
        }
        finally
        {
            background.Cancel();
            applying.CompleteAdding();
            await applyingThread;
            await ticking;
            await approving;
            await publishing;
            if (!browserProcess.HasExited)
            {
                browserProcess.Kill(entireProcessTree: true);
            }
        }
    }

    /// <summary>A desk with a different plan and a different picture for every map.</summary>
    private sealed class Desk : ITabletRemoteMap
    {
        private static readonly TimeSpan SwitchTime = TimeSpan.FromMilliseconds(250);
        private static readonly Dictionary<string, TabletMapArtworkBytes> Pictures = DeskMaps
            .Select((map, index) =>
            {
                // A different width is a different image, and so a different content hash.
                var png = TabletScreenshotHarness.SynthesizePng(64 + (index * 8), 48);
                return (map.Id, Picture: new TabletMapArtworkBytes("image/png", Convert.ToHexStringLower(SHA256.HashData(png)), png));
            })
            .ToDictionary(item => item.Id, item => item.Picture);

        private readonly Lock _gate = new();
        private string _mapId = DeskMaps[0].Id;
        private double _x = 400;
        private double _y = 300;
        private double _zoom = 1;
        private long _version;

        public string? CurrentMapId
        {
            get
            {
                lock (_gate)
                {
                    return _mapId;
                }
            }
        }

        public async Task SelectMapAsync(string mapId)
        {
            if (!Pictures.ContainsKey(mapId))
            {
                return;
            }

            await Task.Delay(SwitchTime).ConfigureAwait(false);
            lock (_gate)
            {
                (_mapId, _x, _y, _zoom) = (mapId, 400, 300, 1);
                _version++;
            }
        }

        public void ApplyView(WorkspaceProjection projection, WorkspaceViewport? camera)
        {
            if (camera is null)
            {
                return;
            }

            lock (_gate)
            {
                (_x, _y, _zoom) = (camera.Center.X, camera.Center.Z, camera.Zoom);
                _version++;
            }
        }

        public (long Version, TabletMapSurface Surface, TabletMapArtworkBytes Artwork) Snapshot(DateTimeOffset now)
        {
            lock (_gate)
            {
                var picture = Pictures[_mapId];
                var surface = new TabletMapSurface(
                    Revision: _version + 1,
                    MapId: _mapId,
                    MapName: DeskMaps.Single(map => map.Id == _mapId).Name,
                    VariantKey: _mapId,
                    TransformVersion: "v1",
                    Plan: new TabletMapPlan(0, 0, 800, 600),
                    Artwork: new TabletMapArtwork(picture.MediaType, picture.ContentSha256, 64, 48),
                    Attribution: [new TabletMapAttribution("Synthetic test artwork", "https://example.invalid/map", "https://example.invalid/license", picture.ContentSha256, "Reviewed")],
                    FloorIds: ["1F"],
                    Layers: [new TabletMapLayer("landmarks", "Landmarks", 0, true)],
                    Objects: [],
                    View: new TabletMapView("1F", _x, _y, _zoom, null, null),
                    Search: null,
                    Message: null,
                    PublishedUtc: now,
                    Maps: DeskMaps);
                return (_version, surface, picture);
            }
        }
    }

    private static async Task Delay(int milliseconds, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(milliseconds, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90); // liveness: the browser starts with the rest of the suite running
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
