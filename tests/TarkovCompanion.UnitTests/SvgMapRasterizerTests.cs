using System.Diagnostics;
using System.Text;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.Infrastructure.Maps;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// What the map rasteriser refuses, and what happens when it is asked to draw elsewhere.
/// </summary>
/// <remarks>
/// The application died on 2026-09-19 with a native access violation (0xc0000005) inside Skia at
/// <c>sk_canvas_draw_picture</c>, reached from this code. A native fault raises no managed
/// exception, so there is nothing here that can assert "it did not crash" — a test that provoked
/// one would take the whole test host with it. What can be asserted is everything that makes the
/// fault less likely and everything that contains it: the bounds the rasteriser now enforces
/// before Skia sees a document, and the child-process arrangement that keeps a fault away from the
/// process that must survive.
/// </remarks>
public sealed class SvgMapRasterizerTests
{
    /// <summary>A document that nests beyond the limit is refused rather than handed over.</summary>
    /// <remarks>
    /// Playback of a recorded picture recurses natively. Running a native stack out is not an
    /// exception; it is the process ending.
    /// </remarks>
    [Fact]
    public async Task ADocumentNestedTooDeeplyIsRefused()
    {
        using var directory = new TemporaryDirectory();
        var builder = new StringBuilder("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 20 10\">");
        for (var depth = 0; depth < 200; depth++)
        {
            builder.Append("<g>");
        }

        builder.Append("<rect width=\"20\" height=\"10\" fill=\"#123456\" />");
        for (var depth = 0; depth < 200; depth++)
        {
            builder.Append("</g>");
        }

        builder.Append("</svg>");
        var svgPath = Path.Combine(directory.Path, "deep.svg");
        await File.WriteAllTextAsync(svgPath, builder.ToString());

        var refusal = await Assert.ThrowsAsync<InvalidDataException>(() => SvgMapRasterizer.CreatePreviewAsync(
            svgPath,
            Path.Combine(directory.Path, "deep.png"),
            CancellationToken.None));

        Assert.Contains("nests deeper", refusal.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(directory.Path, "deep.png")));
    }

    /// <summary>A drawing this rasteriser can handle still produces its full-resolution preview.</summary>
    /// <remarks>
    /// The bounds above are only worth having if they do not refuse the real maps. 20 x 10 is the
    /// shape the other fixtures use, and 4096 along the longer side is the budget this file exists
    /// to fill rather than stay under.
    /// </remarks>
    [Fact]
    public async Task AnOrdinaryDrawingIsStillRasterisedToTheFullBudget()
    {
        using var directory = new TemporaryDirectory();
        var svgPath = Path.Combine(directory.Path, "ordinary.svg");
        await File.WriteAllTextAsync(
            svgPath,
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 20 10\">"
            + "<rect width=\"20\" height=\"10\" fill=\"#123456\" /></svg>");
        var previewPath = Path.Combine(directory.Path, "ordinary.png");

        await SvgMapRasterizer.CreatePreviewAsync(svgPath, previewPath, CancellationToken.None);

        using var rendered = SkiaSharp.SKBitmap.Decode(previewPath);
        Assert.Equal(4096, rendered.Width);
        Assert.Equal(2048, rendered.Height);
    }

    /// <summary>
    /// The rasteriser child does the whole job from its own arguments.
    /// </summary>
    /// <remarks>
    /// This is the body of the child process, called directly. <c>Program.Main</c> hands it every
    /// launch's arguments before it does anything else, and a launch without
    /// <c>--rasterise-svg</c> is an ordinary one.
    /// </remarks>
    [Fact]
    public async Task TheRasterizerChildDrawsWhatItsArgumentsName()
    {
        using var directory = new TemporaryDirectory();
        var svgPath = Path.Combine(directory.Path, "child.svg");
        await File.WriteAllTextAsync(
            svgPath,
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 20 10\">"
            + "<g id=\"Ground_Level\"><rect width=\"20\" height=\"10\" fill=\"#123456\" /></g>"
            + "<g id=\"Upper_Floor\"><rect width=\"20\" height=\"10\" fill=\"#abcdef\" /></g></svg>");
        var previewPath = Path.Combine(directory.Path, "child.png");

        var exitCode = MapRasterizerHost.TryRun([
            SvgRasterizerHost.SvgOption, svgPath,
            SvgRasterizerHost.PreviewOption, previewPath,
            SvgRasterizerHost.LayerOption, "Upper_Floor",
        ]);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(previewPath));
    }

    /// <summary>An ordinary launch is not a rasteriser launch.</summary>
    [Fact]
    public void ALaunchWithoutTheRasterizerOptionIsNotARasterizerLaunch()
    {
        Assert.Null(MapRasterizerHost.TryRun(["--ui-shell", "v2-a"]));
        Assert.Null(MapRasterizerHost.TryRun([]));
    }

    /// <summary>A child asked for a document it cannot draw refuses, and says why on stderr.</summary>
    [Fact]
    public void TheRasterizerChildRefusesADocumentItCannotDraw()
    {
        using var directory = new TemporaryDirectory();
        var original = Console.Error;
        var captured = new StringWriter();
        try
        {
            Console.SetError(captured);
            var exitCode = MapRasterizerHost.TryRun([
                SvgRasterizerHost.SvgOption, Path.Combine(directory.Path, "absent.svg"),
                SvgRasterizerHost.PreviewOption, Path.Combine(directory.Path, "absent.png"),
            ]);

            Assert.Equal(SvgRasterizerHost.RefusedExitCode, exitCode);
        }
        finally
        {
            Console.SetError(original);
        }

        Assert.Contains("absent.svg", captured.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A child that ran and failed is reported, with its exit code and its own words.
    /// </summary>
    /// <remarks>
    /// The one property that makes the child process worth having: the parent reads the failure
    /// rather than sharing it. An access violation reaches this path as exit code -1073741819, and
    /// the message names it, because that number in a log is the most useful fact about a crash of
    /// this kind and the least recognisable.
    /// </remarks>
    [Fact]
    public async Task AChildThatFailsIsReportedRatherThanRetriedHere()
    {
        using var directory = new TemporaryDirectory();
        var host = FailingHost(3, "the drawing could not be read");

        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => RunChildAsync(
            host,
            Path.Combine(directory.Path, "any.svg"),
            Path.Combine(directory.Path, "any.png")));

        Assert.Contains("exit code 3", failure.Message, StringComparison.Ordinal);
        Assert.Contains("the drawing could not be read", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>A host that cannot be started is a different failure, and says so.</summary>
    /// <remarks>
    /// Because the caller acts on the difference. A child that will not start says nothing about
    /// the drawing, so the cache rasterises in this process instead; a child that started and died
    /// says the opposite, and retrying it here is how the application would be killed by the fault
    /// the child exists to contain.
    /// </remarks>
    [Fact]
    public async Task AHostThatCannotBeStartedIsDistinguishedFromAChildThatFailed()
    {
        using var directory = new TemporaryDirectory();
        var host = new SvgRasterizerHost(
            Path.Combine(directory.Path, "no-such-executable"),
            [],
            TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<SvgRasterizerHostUnavailableException>(() => RunChildAsync(
            host,
            Path.Combine(directory.Path, "any.svg"),
            Path.Combine(directory.Path, "any.png")));
    }

    /// <summary>
    /// A child that never finishes is stopped at its deadline.
    /// </summary>
    /// <remarks>
    /// Two wrong stand-ins before this one, both of which failed by not hanging, and the test said
    /// something different each time rather than saying that. So the stand-in is now chosen to
    /// depend on nothing, and the test checks that it really did hang.
    ///
    /// <c>cmd.exe /c pause</c> was first: <c>pause</c> does not pause when its output is
    /// redirected — which this rasteriser always does, so it can read the child's stderr — so the
    /// child exited in thirty milliseconds with code 0 and the call returned successfully.
    /// <c>ping -n 30</c> was second, and failed for a different reason:
    /// <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/> quotes every entry
    /// separately, so <c>cmd</c> received <c>/c "ping" "-n" "30" "127.0.0.1"</c> and its
    /// leading-quote rule mangled that into something it could not run — exit code 1, in 142
    /// milliseconds. One string after <c>/c</c> is the shape that survives, which is why
    /// <see cref="FailingHost"/> has always worked.
    ///
    /// <c>for /l %i in (1,0,2)</c> counts from 1 towards 2 in steps of 0, so it never arrives.
    /// It is a <c>cmd</c> built-in: no PATH lookup, no external binary, nothing to be absent from
    /// a runner image. <c>sleep</c> is POSIX, so the other side needs no equivalent.
    ///
    /// The assertions are the message and a *lower* bound on the elapsed time. The message
    /// separates the two outcomes an unbounded regression chooses between — "was stopped" against
    /// "exit code" — with no clock. The lower bound is what both previous versions lacked: a
    /// stand-in that exits early can no longer look like a deadline that worked. It is load-safe,
    /// because load only makes the elapsed time longer.
    /// </remarks>
    [Fact]
    public async Task AChildThatHangsIsStoppedAtItsDeadline()
    {
        using var directory = new TemporaryDirectory();
        var deadline = TimeSpan.FromMilliseconds(500);
        var host = OperatingSystem.IsWindows()
            ? new SvgRasterizerHost("cmd.exe", ["/c", "for /l %i in (1,0,2) do @rem"], deadline)
            : new SvgRasterizerHost("/bin/sh", ["-c", "sleep 30"], deadline);
        var clock = Stopwatch.StartNew();

        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => RunChildAsync(
            host,
            Path.Combine(directory.Path, "any.svg"),
            Path.Combine(directory.Path, "any.png")));

        Assert.Contains("was stopped", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("exit code", failure.Message, StringComparison.Ordinal);
        Assert.True(
            clock.Elapsed >= deadline,
            $"The stand-in stopped after {clock.Elapsed.TotalMilliseconds:0} ms, inside its "
            + $"{deadline.TotalMilliseconds:0} ms deadline, so it did not hang and this test proved nothing.");
    }

    /// <summary>A shell that prints to stderr and exits with the given code.</summary>
    /// <summary>
    /// The real thing: this application's own executable, started as a child, draws the floor.
    /// </summary>
    /// <remarks>
    /// #452. Every other test here either calls the child's body in this process or stands a shell
    /// in for the child, so none of them proved that the executable the application re-launches
    /// reaches the rasteriser and writes a picture. This one starts it. The executable is built
    /// whenever these tests are, because the test project references the application, so its
    /// absence is a failure rather than a reason to skip.
    /// </remarks>
    [Fact]
    public async Task TheApplicationsOwnExecutableDrawsAFloorAsAChildProcess()
    {
        using var directory = new TemporaryDirectory();
        var svgPath = Path.Combine(directory.Path, "real-child.svg");
        await File.WriteAllTextAsync(
            svgPath,
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 20 10\">"
            + "<g id=\"Ground_Level\"><rect width=\"20\" height=\"10\" fill=\"#123456\" /></g>"
            + "<g id=\"Upper_Floor\"><rect width=\"20\" height=\"10\" fill=\"#abcdef\" /></g></svg>");
        var previewPath = Path.Combine(directory.Path, "real-child.png");

        await OutOfProcessSvgRasterizer.CreatePreviewAsync(
            ApplicationHost(),
            svgPath,
            previewPath,
            "Upper_Floor",
            CancellationToken.None);

        var png = await File.ReadAllBytesAsync(previewPath);
        Assert.True(png.Length > 8);
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], png[..4]);
    }

    /// <summary>The real child refuses a drawing it cannot read, and the parent hears why.</summary>
    [Fact]
    public async Task TheApplicationsOwnExecutableRefusesADrawingItCannotRead()
    {
        using var directory = new TemporaryDirectory();
        var svgPath = Path.Combine(directory.Path, "not-a-drawing.svg");
        await File.WriteAllTextAsync(svgPath, "this is not xml");
        var previewPath = Path.Combine(directory.Path, "not-a-drawing.png");

        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => OutOfProcessSvgRasterizer.CreatePreviewAsync(
            ApplicationHost(),
            svgPath,
            previewPath,
            visibleLayer: null,
            CancellationToken.None));

        Assert.Contains("refused the drawing", failure.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(previewPath));
    }

    /// <summary>
    /// The in-process rasteriser never runs twice at once, however many callers arrive together.
    /// </summary>
    /// <remarks>
    /// #452. The fallback for a child that cannot be started is this process, and three 64 MiB
    /// surfaces drawn at once is how it was first brought down. Eight callers, and the count the
    /// rasteriser keeps of how many were inside Skia at once must still read one.
    /// </remarks>
    [Fact]
    public async Task RasterisingInThisProcessNeverOverlapsWithItself()
    {
        using var directory = new TemporaryDirectory();
        var svgPath = Path.Combine(directory.Path, "overlap.svg");
        await File.WriteAllTextAsync(
            svgPath,
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 20 10\">"
            + "<rect width=\"20\" height=\"10\" fill=\"#123456\" /></svg>");

        await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Task.Run(() => SvgMapRasterizer.CreatePreviewAsync(
            svgPath,
            Path.Combine(directory.Path, $"overlap-{index}.png"),
            CancellationToken.None))));

        Assert.Equal(1, SvgMapRasterizer.MostDrawingAtOnce);
        Assert.Equal(8, Directory.GetFiles(directory.Path, "overlap-*.png").Length);
    }

    /// <summary>The application's own build output, which building these tests also builds.</summary>
    /// <remarks>
    /// Not the copy beside the tests. That one starts and then cannot find half its dependencies,
    /// because the test host's output is laid out for the test host; the application's own output
    /// directory is the only place its executable is actually runnable from.
    /// </remarks>
    private static SvgRasterizerHost ApplicationHost()
    {
        // .../tests/TarkovCompanion.UnitTests/bin/<configuration>/<framework>/
        var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var framework = output.Name;
        var configuration = output.Parent!.Name;
        var root = output;
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "TarkovCompanion.sln")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var executable = Path.Combine(
            root.FullName,
            "src",
            "TarkovCompanion.App",
            "bin",
            configuration,
            framework,
            OperatingSystem.IsWindows() ? "TarkovCompanion.exe" : "TarkovCompanion");
        Assert.True(File.Exists(executable), $"The application's host executable is not where its build puts it: {executable}");
        return new(executable, [], TimeSpan.FromSeconds(120));
    }

    private static SvgRasterizerHost FailingHost(int exitCode, string message) => OperatingSystem.IsWindows()
        ? new("cmd.exe", ["/c", $"echo {message} 1>&2 & exit {exitCode}"], TimeSpan.FromSeconds(20))
        : new("/bin/sh", ["-c", $"echo '{message}' >&2; exit {exitCode}"], TimeSpan.FromSeconds(20));

    /// <summary>
    /// Launches a child and reads its outcome, which is all the parent side does.
    /// </summary>
    /// <remarks>
    /// The stand-in hosts above ignore the rasteriser options and only choose how to fail, which is
    /// the point: what is under test here is how the parent reads an exit code, not what a real
    /// child draws. Which of these failures sends the work back into this process is the cache's
    /// decision, and is covered by <c>TarkovDevMapTests</c>.
    /// </remarks>
    private static async Task RunChildAsync(SvgRasterizerHost host, string svgPath, string previewPath) =>
        await OutOfProcessSvgRasterizer
            .CreatePreviewAsync(host, svgPath, previewPath, visibleLayer: null, CancellationToken.None);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "tarkov-rasterizer-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }
}
