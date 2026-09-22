using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Application.Services.StashScan;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Inventory;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Core.Domain.Recognition.Grid;
using TarkovCompanion.Infrastructure.Recognition.Grid;
using TarkovCompanion.Infrastructure.Recognition;
using TarkovCompanion.UnitTests.V2Contracts;

namespace TarkovCompanion.UnitTests.StashScan;

public sealed class StashScrollPlacementTests
{
    [Fact]
    public void Scrollbar_reader_reports_the_visible_thumb_position()
    {
        const int width = 800;
        const int height = 400;
        var pixels = new byte[width * height * 4];
        for (var index = 3; index < pixels.Length; index += 4)
        {
            pixels[index] = 255;
        }

        for (var y = 150; y < 200; y++)
        {
            for (var x = 737; x < 743; x++)
            {
                var offset = ((y * width) + x) * 4;
                pixels[offset] = 160;
                pixels[offset + 1] = 160;
                pixels[offset + 2] = 160;
            }
        }

        var image = new CapturedImage(
            pixels,
            width,
            height,
            width * 4,
            PixelFormat.Bgra8888,
            StashScanMeasurement.ObservedUtc,
            "fixture");

        var position = StashScrollbarReader.Read(image, new ContainerGridSpec(new PixelRect(100, 20, 630, 300), 10, 4));

        Assert.NotNull(position);
        Assert.InRange(position.Value, 0.51, 0.53);
    }

    [Fact]
    public void Scroll_order_places_page_sized_gaps_and_overlap_stays_first()
    {
        var session = new CaptureSessionId(Guid.Parse("04bd2557-a943-4db0-9f98-7a0d44358c63"));
        var image = new CapturedImage(
            new byte[4],
            1,
            1,
            4,
            PixelFormat.Bgra8888,
            StashScanMeasurement.ObservedUtc,
            "fixture");
        StashScanCaptureFrame Frame(int ordinal, double scroll, bool first) =>
            StashScanMeasurement.Frame(
                session,
                ordinal,
                image,
                new GridReconstructionResult(
                    GridReconstructionOutcome.Complete,
                    InventoryGridSurface.Stash,
                    V2ContractTestData.Grid(),
                    [],
                    [],
                    scroll),
                first);
        StashScanAssemblyRequest Request(IReadOnlyList<StashScanCaptureFrame> frames) => new(
            "scroll-result",
            "scroll-snapshot",
            session,
            new InventoryProfileScope(Guid.Parse("a359a14f-90c1-4618-8cb4-d1b27a8346c0"), "wipe", "Pvp"),
            "data",
            StashScanMeasurement.ObservedUtc,
            frames);

        StashScanCaptureFrame[] frames = [Frame(0, 0.1, true), Frame(1, 0.5, false), Frame(2, 0.9, false)];
        var assembler = new StashScanAssembler();
        var aligned = new StashLayoutAligner().AddLayoutOrigins(
            frames,
            assembler.Assemble(Request(frames)),
            StashScanMeasurement.ObservedUtc);
        Assert.Equal(4, aligned.Single(frame => frame.CaptureOrdinal == 1).OriginHint!.Value!.Value.Row);
        Assert.Equal(8, aligned.Single(frame => frame.CaptureOrdinal == 2).OriginHint!.Value!.Value.Row);
        Assert.Equal(
            "stash.origin.scroll-order",
            aligned.Single(frame => frame.CaptureOrdinal == 1).OriginHint!.Provenance.SourceIdentifier);
    }
}
