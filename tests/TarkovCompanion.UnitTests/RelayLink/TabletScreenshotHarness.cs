using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.UnitTests.RelayDeviceSecurity;

namespace TarkovCompanion.UnitTests.RelayLink;

/// <summary>
/// Not a gate: a LOOK tool. Drives the real tablet page (#289's real-browser harness, extended)
/// through every state a player actually reaches — before pairing, mid-pairing, paired in each of
/// Follow/Control/Independent with a real published map surface (waypoints, a ping, quest
/// objective pins, one placed by the player), a rejected command's notice, and the desktop going
/// quiet — and screenshots each one at three viewports (tablet landscape, portrait, phone).
/// </summary>
/// <remarks>
/// Skipped unless both a headless browser is present (see <see cref="HasHeadlessBrowser"/>, same
/// check as <c>RelayLinkRealBrowserTests</c>) and <c>TABLET_SCREENSHOT_DIR</c> names where to
/// write the PNGs — this is a capture tool a person runs on purpose, not part of the ordinary
/// suite, so an empty environment variable is "do nothing" rather than "fail".
/// </remarks>
[Collection(RelayAdminKeyCollection.Name)]
public sealed class TabletScreenshotHarness
{
    [Fact]
    public async Task CaptureTabletScreenshots()
    {
        if (!HasHeadlessBrowser())
        {
            return;
        }

        var outDir = Environment.GetEnvironmentVariable("TABLET_SCREENSHOT_DIR");
        if (string.IsNullOrWhiteSpace(outDir))
        {
            return;
        }

        Directory.CreateDirectory(outDir);

        using var certificate = CreateSelfSignedCertificate("localhost");
        // Truncated to the millisecond: the protocol's own timestamps require exact millisecond
        // precision (see the same remark on RelayMarksBridge's `Now()` helper). Measured: a clock
        // started from a bare `DateTimeOffset.UtcNow` (sub-millisecond ticks) makes
        // PublishMapSurfaceAsync fail with a bare 500 from the relay and no other symptom — every
        // other RelayLink test either starts its clock from a whole-second value
        // (RelaySecurityTestFactory.Now) or never reaches the code path that trips on it.
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

        var artwork = SynthesizePng(1600, 1200);
        var artworkSha = Convert.ToHexString(SHA256.HashData(artwork)).ToLowerInvariant();
        var surface = BuildCustomsSurface(artworkSha, clock.GetUtcNow());

        await ((AsyncDelegateCommand)desktop.Panel.StartPairingCommand).ExecuteAsync();
        Assert.True(desktop.Panel.IsAwaitingTablet, desktop.Panel.StatusMessage);
        var pairingCode = desktop.Panel.PairingCode!;

        // Nothing here pushes updates to the relay by itself (that is an App-level timer this bare
        // harness does not include), so this stands in for it for the life of the capture: without
        // it, a command the browser sends (requestControl, a mark, a navigate) would sit on the
        // relay forever and the browser would wait fifteen seconds and say the desktop is not
        // answering.
        //
        // It also keeps this fake clock roughly in step with real time (measured: it does not
        // otherwise move at all): the browser's own commands carry a real wall-clock issuedUtc,
        // and CanonicalStateMachine refuses a command "issued in the future" of the desktop's own
        // clock — invisibly, as a command that never gets an answer, once the two have drifted
        // more than a few seconds apart, which a browser-driven capture this long always would.
        using var pollLoop = new CancellationTokenSource();
        var polling = Task.Run(async () =>
        {
            while (!pollLoop.IsCancellationRequested)
            {
                try
                {
                    await desktop.Bridge.PollOnceAsync(pollLoop.Token);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception)
                {
                    // A poll tick failing is not this test's business; the next tick tries again.
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

        var scriptPath = Path.Combine(RepositoryRoot(), "scripts", "capture-tablet-states.cjs");
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
        startInfo.ArgumentList.Add(pairingCode);
        startInfo.ArgumentList.Add("Raid tablet");
        startInfo.ArgumentList.Add(outDir);
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
            // Approve the pairing exactly as a person watching the desktop panel would.
            await UntilAsync(() => desktop.Panel.IsAwaitingApproval || browserProcess.HasExited, "the tablet's pairing request");
            AssertNotExited(browserProcess, stdoutLog);
            Assert.True(desktop.Panel.IsAwaitingApproval, desktop.Panel.StatusMessage);
            await ((AsyncDelegateCommand)desktop.Panel.ApproveCommand).ExecuteAsync();

            // Published once this device is paired and registered with the relay, so the browser's
            // very first surface read already has it — the same order every other RelayLink
            // map-surface test uses.
            var published = await desktop.Bridge.PublishMapSurfaceAsync(
                TabletMapSurfaceJson.Serialize(surface),
                new TabletMapArtworkBytes("image/png", artworkSha, artwork));
            Assert.True(published);

            // Grant control the moment the browser asks for it, the same way the desktop's own
            // "Allow" button would (CompanionPairingViewModel.AllowControlCommand does the same
            // ResolveControlCommand this does).
            await UntilAsync(
                () => desktop.Authority.Snapshot.CanonicalState.DeviceModes.PendingControl is not null || browserProcess.HasExited,
                "the tablet's control request");
            if (!browserProcess.HasExited)
            {
                var pendingControl = desktop.Authority.Snapshot.CanonicalState.DeviceModes.PendingControl!;
                var modesRevision = desktop.Authority.Snapshot.CanonicalState.DeviceModes.Cursor.Revision.Value;
                var disposition = await desktop.Bridge.ApplyDesktopCommandAsync(new ResolveControlCommand(
                    new CommandId(Guid.NewGuid()),
                    new AggregateRevision(modesRevision + 1),
                    clock.GetUtcNow(),
                    clock.GetUtcNow().AddMinutes(1),
                    pendingControl.RequestCommandId,
                    approved: true,
                    new ControlLeaseId(Guid.NewGuid())));
                Assert.Equal(CommandDisposition.Applied, disposition);
            }

            // "Desktop offline": node asks, over stdout, once it is ready to check; this advances
            // the fake clock well past the 15-second silence threshold and stops the poll loop
            // (so nothing refreshes "last seen" again), then tells node to go. A real 15-second
            // wait would work too; this is the same fact without spending it.
            await UntilAsync(() => ContainsLine(stdoutLog, stdoutGate, "READY_FOR_OFFLINE") || browserProcess.HasExited, "node to ask for the offline step");
            if (!browserProcess.HasExited)
            {
                pollLoop.Cancel();
                await polling;
                clock.Advance(TimeSpan.FromMinutes(2));
                await browserProcess.StandardInput.WriteLineAsync("GO");
                await browserProcess.StandardInput.FlushAsync();
            }

            await Task.WhenAny(browserProcess.WaitForExitAsync(), Task.Delay(TimeSpan.FromSeconds(60)));
            var stderr = await stderrTask;
            string stdout;
            lock (stdoutGate)
            {
                stdout = string.Join('\n', stdoutLog);
            }

            Assert.True(browserProcess.HasExited, $"The headless browser did not finish in time.\nstdout:\n{stdout}\nstderr:\n{stderr}");
            Assert.True(browserProcess.ExitCode == 0, $"exit code {browserProcess.ExitCode}\nstdout:\n{stdout}\nstderr:\n{stderr}");

            // #407: proof, not assumption, that a Control-mode tablet sending a different mapId
            // reaches the canonical desktop — the fact TabletMapSurfacePublisher.
            // OnDesktopWorkspaceRequested (a full Avalonia app, out of reach of this harness) acts
            // on to actually call RaidCockpitViewModel.SelectMapAsync in the real application.
            Assert.Equal("woods", desktop.Authority.Snapshot.CanonicalState.Workspace.Projection.MapId);
        }
        finally
        {
            if (!pollLoop.IsCancellationRequested)
            {
                pollLoop.Cancel();
            }

            await polling;
            if (!browserProcess.HasExited)
            {
                browserProcess.Kill(entireProcessTree: true);
            }
        }
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

    /// <summary>Customs: a few waypoints, a ping, two quest objective pins (one placed by the
    /// player), and two search results (one flagged Allergic) — everything task 1's screenshots
    /// and task 4's chip need in one publish.</summary>
    private static TabletMapSurface BuildCustomsSurface(string artworkSha, DateTimeOffset now) => new(
        Revision: 1,
        MapId: "customs",
        MapName: "Customs",
        VariantKey: "default",
        TransformVersion: "v1",
        Plan: new TabletMapPlan(0, 0, 1000, 1000),
        Artwork: new TabletMapArtwork("image/png", artworkSha, 1600, 1200),
        Attribution: [new TabletMapAttribution("Synthetic test artwork", "https://example.invalid/customs", "https://example.invalid/license", artworkSha, "Reviewed")],
        FloorIds: ["1F", "2F"],
        Layers: [new TabletMapLayer("landmarks", "Landmarks", 0, true), new TabletMapLayer("loot", "Loot", 1, true)],
        Objects:
        [
            new("waypoint-1", "landmarks", "Waypoint", "PersonalPlan", "1", null, [150, 150], [], null, false),
            new("waypoint-2", "landmarks", "Waypoint", "PersonalPlan", "2", null, [500, 200], [], null, false),
            new("waypoint-3", "landmarks", "Waypoint", "PersonalPlan", "3", null, [850, 600], [], null, false),
            new("ping-1", "landmarks", "Ping", "PersonalPlan", "Ping", null, [300, 700], [], null, false),
            new("objective-a", "landmarks", "QuestObjective", "StaticReference", "A", "Find the stash", [700, 850], [], null, false),
            // Issue 379: the one this tablet placed itself — truth UserAuthored draws the dashed
            // outline drawPin gives a player-placed objective.
            new("objective-b", "landmarks", "QuestObjective", "UserAuthored", "B", "Marked by you", [200, 900], [], null, false),
        ],
        View: new TabletMapView("1F", 500, 500, 1, null, null),
        Search: new TabletSearch(
            "aid",
            [
                new TabletSearchResult("item-sugar", "Pack of sugar", "Sugar", 4500, 3800, IsAllergic: false),
                new TabletSearchResult("item-salewa", "Salewa first aid kit", "Salewa", 28000, 24000, IsAllergic: true),
            ]),
        Message: null,
        PublishedUtc: now);

    /// <summary>
    /// A plain RGB PNG this test builds by hand (no imaging library is a dependency of this
    /// repository) — a grid, not a photograph, precisely so a pin's tip lands on a countable line
    /// rather than somewhere on a satellite photo nobody can measure from a screenshot.
    /// </summary>
    private static byte[] SynthesizePng(int width, int height)
    {
        var stride = 1 + (width * 3);
        var raw = new byte[height * stride];
        for (var y = 0; y < height; y++)
        {
            var rowStart = y * stride;
            raw[rowStart] = 0; // filter: none
            for (var x = 0; x < width; x++)
            {
                var onGrid = x % 100 < 2 || y % 100 < 2;
                byte r = onGrid ? (byte)90 : (byte)(30 + (y * 40 / height));
                byte g = onGrid ? (byte)90 : (byte)(55 + (x * 30 / width));
                byte b = onGrid ? (byte)90 : (byte)35;
                var pixel = rowStart + 1 + (x * 3);
                raw[pixel] = r;
                raw[pixel + 1] = g;
                raw[pixel + 2] = b;
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        WriteChunk(png, "IHDR", BuildIhdr(width, height));
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static byte[] BuildIhdr(int width, int height)
    {
        var ihdr = new byte[13];
        WriteBigEndian(ihdr, 0, width);
        WriteBigEndian(ihdr, 4, height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 2; // color type: truecolor RGB
        ihdr[10] = 0; // compression
        ihdr[11] = 0; // filter
        ihdr[12] = 0; // interlace
        return ihdr;
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        WriteBigEndian(length, 0, data.Length);
        stream.Write(length);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        var crc = Crc32(typeBytes, data);
        var crcBytes = new byte[4];
        WriteBigEndian(crcBytes, 0, unchecked((int)crc));
        stream.Write(crcBytes);
    }

    private static void WriteBigEndian(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static readonly uint[] Crc32Table = BuildCrc32Table();

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in type)
        {
            crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        foreach (var b in data)
        {
            crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
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
