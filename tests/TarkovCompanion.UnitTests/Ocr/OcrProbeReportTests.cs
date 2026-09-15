using System.Text.Json.Nodes;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.UnitTests.Ocr;

public sealed class OcrProbeReportTests
{
    [Fact]
    public void MachineReportKeepsUnknownConfidenceDistinctFromMeasuredZero()
    {
        var bounds = new PixelRect(10, 20, 30, 40);
        var pass = new OcrProbePass(
            "cell-0",
            "fixture",
            "complete",
            null,
            bounds,
            1920,
            1080,
            30,
            40,
            1,
            1,
            1,
            1,
            2_073_600,
            8_294_400,
            12.5,
            2,
            1,
            1,
            "Container",
            0.65,
            false,
            false,
            [new("unscored", bounds, null), new("measured zero", bounds, 0)],
            []);
        var report = new OcrProbeReport(
            OcrProbeReport.CurrentSchemaVersion,
            "cells",
            new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero),
            new(1920, 1080, new DateTimeOffset(2026, 9, 14, 11, 59, 59, TimeSpan.Zero)),
            bounds,
            null,
            [new(0, bounds, bounds)],
            [new("fixture", true, null, [pass])]);

        var document = JsonNode.Parse(OcrProbe.SerializeReport(report))!.AsObject();
        var lines = document["engines"]![0]!["passes"]![0]!["lines"]!.AsArray();

        Assert.Null(lines[0]!["confidence"]);
        Assert.Equal(0, lines[1]!["confidence"]!.GetValue<double>());
        Assert.Equal("tarkov-companion.ocr-probe.v1", document["schemaVersion"]!.GetValue<string>());
        Assert.DoesNotContain("sourcePath", document.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("screenshotPath", document.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }
}
