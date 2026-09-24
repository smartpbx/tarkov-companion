using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using TarkovCompanion.App.Services.V2;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.UnitTests.RelayDeviceSecurity;

namespace TarkovCompanion.UnitTests.RelayLink;

/// <summary>
/// #800: a tablet in Control switched the desktop to the wrong map, and zoom and pan did not
/// follow. A real tablet page in headless Chromium, a real in-process relay and desktop bridge,
/// and the desktop's own <see cref="TabletRemoteWorkspaceApplier"/> over a desk that behaves like
/// the cockpit: a switch takes a while (the scene rebuild), an unknown id is ignored (as
/// <c>MapViewModel.FollowRaidAsync</c> ignores it), and it publishes what it shows.
/// </summary>
[Collection(RelayAdminKeyCollection.Name)]
public sealed class TabletControlMapSyncBrowserTests : RealBrowserTestHarness
{
    /// <summary>
    /// The desktop's picker: every location of the tarkov.dev map catalog with a drawable plan
    /// (src/data/maps.json, read 2026-09-24), under the name the catalog parser gives it, in the
    /// order <c>RaidCockpitViewModel.RebuildMapPicker</c> sorts them.
    /// </summary>
    private static readonly TabletMapChoice[] DeskMaps =
    [
        .. new TabletMapChoice[]
        {
            new("streets-of-tarkov", "Streets Of Tarkov"),
            new("ground-zero", "Ground Zero"),
            new("customs", "Customs"),
            new("factory", "Factory"),
            new("icebreaker", "Icebreaker"),
            new("interchange", "Interchange"),
            new("the-lab", "The Lab"),
            new("the-labyrinth", "The Labyrinth"),
            new("lighthouse", "Lighthouse"),
            new("reserve", "Reserve"),
            new("shoreline", "Shoreline"),
            new("terminal", "Terminal"),
            new("woods", "Woods"),
        }.OrderBy(map => map.Name, StringComparer.OrdinalIgnoreCase),
    ];

    [Fact]
    public void TheDeskMapIdsAreTheGameDataNormalizedNames()
    {
        // The seed catalog is on the dev host only; CI has the list above and nothing to check it by.
        var seed = Environment.GetEnvironmentVariable("TARKOV_SEED_DATABASE") is { Length: > 0 } configured
            ? configured
            : "/root/orca/seed/catalog-2026-09-14.db";
        if (!File.Exists(seed))
        {
            return;
        }

        using var connection = new SqliteConnection($"Data Source={seed};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT json_extract(source_json, '$.normalizedName') FROM maps";
        var normalized = new HashSet<string>(StringComparer.Ordinal);
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                normalized.Add(reader.GetString(0));
            }
        }

        Assert.All(DeskMaps, map => Assert.Contains(map.Id, normalized));
    }

    [RealBrowserFact]
    public async Task EveryMapInTheTabletsPickerLandsTheDeskThereAndTheDeskFollowsPanAndZoom()
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

        using var ui = new SingleThreadContext();
        var desk = new Desk();
        var applier = new TabletRemoteWorkspaceApplier(desk);
        desktop.Bridge.DesktopWorkspaceRequested += projection => ui.Post(
            _ =>
            {
                if (projection.Workspace == WorkspaceKind.Raid)
                {
                    _ = applier.SubmitAsync(projection);
                }
            },
            null);

        using var background = new CancellationTokenSource();
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

        // What the desk shows reaches the tablet as its published surface, as the publisher's does.
        var publishing = Task.Run(async () =>
        {
            long published = -1;
            while (!background.IsCancellationRequested)
            {
                var (version, surface) = desk.Snapshot(clock.GetUtcNow());
                if (version != published)
                {
                    try
                    {
                        if (await desktop.Bridge.PublishMapSurfaceAsync(TabletMapSurfaceJson.Serialize(surface), artwork: null))
                        {
                            published = version;
                        }
                    }
                    catch (Exception) when (!background.IsCancellationRequested)
                    {
                    }
                }

                await Delay(20, background.Token);
            }
        });

        var scriptPath = Path.Combine(RepositoryRoot(), "scripts", "test-tablet-control-map-sync.cjs");
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

            // The tablet offers exactly the desk's picker: same ids, same names, same order.
            var optionsLine = lines.FirstOrDefault(line => line.StartsWith("OPTIONS ", StringComparison.Ordinal));
            Assert.True(optionsLine is not null, $"No OPTIONS line.\nstdout:\n{stdout}\nstderr:\n{stderr}");
            var options = JsonSerializer.Deserialize<string[][]>(optionsLine!["OPTIONS ".Length..])!;
            var expected = string.Join(", ", DeskMaps.Select(map => $"{map.Id}={map.Name}"));
            var offered = string.Join(", ", options.Select(option => $"{option[0]}={option[1]}"));
            var failLines = Regex.Matches(stdout, "^CHECK:FAIL:.*$", RegexOptions.Multiline).Select(m => m.Value).ToArray();
            Assert.True(
                expected == offered && browserProcess.ExitCode == 0 && lines.Contains("ALL_DONE"),
                $"desk picker:   {expected}\ntablet picker: {offered}\nexit code {browserProcess.ExitCode}\n" +
                $"failed checks ({failLines.Length}):\n{string.Join('\n', failLines)}\nstdout:\n{stdout}\nstderr:\n{stderr}");

            // And the desk itself, not only what it published: on the last map, where the tablet is.
            var final = lines.Single(line => line.StartsWith("FINAL ", StringComparison.Ordinal)).Split(' ');
            var (mapId, x, y, zoom) = desk.Current;
            Assert.Equal(DeskMaps[^1].Id, final[1]);
            Assert.Equal(final[1], mapId);
            Assert.Equal(double.Parse(final[2], CultureInfo.InvariantCulture), x, 0.01);
            Assert.Equal(double.Parse(final[3], CultureInfo.InvariantCulture), y, 0.01);
            Assert.Equal(double.Parse(final[4], CultureInfo.InvariantCulture), zoom, 0.001);
        }
        finally
        {
            background.Cancel();
            await ticking;
            await approving;
            await publishing;
            if (!browserProcess.HasExited)
            {
                browserProcess.Kill(entireProcessTree: true);
            }
        }
    }

    /// <summary>
    /// The desktop's map as the tablet sees it. Each map has its own plan rectangle, so a camera
    /// read in one map's space and applied in another's shows up as a mismatch.
    /// </summary>
    private sealed class Desk : ITabletRemoteMap
    {
        private static readonly TimeSpan SwitchTime = TimeSpan.FromMilliseconds(250);
        private readonly Lock _gate = new();
        private string _mapId = "customs";
        private double _x = Plan("customs").Center.X;
        private double _y = Plan("customs").Center.Y;
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

        public (string MapId, double X, double Y, double Zoom) Current
        {
            get
            {
                lock (_gate)
                {
                    return (_mapId, _x, _y, _zoom);
                }
            }
        }

        public async Task SelectMapAsync(string mapId)
        {
            // As MapViewModel.FollowRaidAsync: an id the catalog does not carry changes nothing.
            if (DeskMaps.FirstOrDefault(map => string.Equals(map.Id, mapId, StringComparison.OrdinalIgnoreCase)) is not { } map)
            {
                return;
            }

            await Task.Delay(SwitchTime).ConfigureAwait(true);
            var plan = Plan(map.Id);
            lock (_gate)
            {
                // A new map opens fitted: its plan's centre, whole plan in view.
                (_mapId, _x, _y, _zoom) = (map.Id, plan.Center.X, plan.Center.Y, 1);
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

        public (long Version, TabletMapSurface Surface) Snapshot(DateTimeOffset now)
        {
            lock (_gate)
            {
                var plan = Plan(_mapId);
                return (_version, new TabletMapSurface(
                    Revision: _version + 1,
                    MapId: _mapId,
                    MapName: DeskMaps.Single(map => map.Id == _mapId).Name,
                    VariantKey: _mapId,
                    TransformVersion: "v1",
                    Plan: plan.Rect,
                    Artwork: null,
                    Attribution: [],
                    FloorIds: ["1F"],
                    Layers: [new TabletMapLayer("landmarks", "Landmarks", 0, true)],
                    Objects: [],
                    View: new TabletMapView("1F", _x, _y, _zoom, null, null),
                    Search: null,
                    Message: null,
                    PublishedUtc: now,
                    Maps: DeskMaps));
            }
        }

        private static (TabletMapPlan Rect, (double X, double Y) Center) Plan(string mapId)
        {
            var index = Array.FindIndex(DeskMaps, map => map.Id == mapId);
            var minimumX = -400 + (index * 130);
            var minimumY = -300 + (index * 70);
            var width = 800 + (index * 90);
            var height = 600 + (index * 60);
            return (
                new TabletMapPlan(minimumX, minimumY, minimumX + width, minimumY + height),
                (minimumX + (width / 2.0), minimumY + (height / 2.0)));
        }
    }

    /// <summary>One thread that runs every posted callback in order, as the desktop's UI thread does.</summary>
    private sealed class SingleThreadContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];
        private readonly Thread _thread;

        public SingleThreadContext()
        {
            _thread = new Thread(() =>
            {
                SetSynchronizationContext(this);
                foreach (var (callback, state) in _queue.GetConsumingEnumerable())
                {
                    try
                    {
                        callback(state);
                    }
                    catch (Exception)
                    {
                        // A failed apply is a desk that did not move, which the checks report.
                    }
                }
            })
            {
                IsBackground = true,
                Name = "desk-ui",
            };
            _thread.Start();
        }

        public override void Post(SendOrPostCallback d, object? state)
        {
            if (!_queue.IsAddingCompleted)
            {
                _queue.Add((d, state));
            }
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(5));
            _queue.Dispose();
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
