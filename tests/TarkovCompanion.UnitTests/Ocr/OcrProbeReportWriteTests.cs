using System.Text.Json.Nodes;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.UnitTests.Ocr;

/// <summary>
/// A probe report is written whole or not at all: cancellation at any point before the move leaves
/// the destination exactly as it was and no partial file beside it.
/// </summary>
public sealed class OcrProbeReportWriteTests : IDisposable
{
    private const string PreviousReport = "{\"schemaVersion\":\"previous\"}";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "tarkov-probe-report-" + Guid.NewGuid().ToString("N"));

    public OcrProbeReportWriteTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task ACompletedWriteReplacesThePreviousReportWhole()
    {
        var destination = Destination(withPreviousReport: true);

        await OcrProbe.WriteReportAsync(destination, Report(), CancellationToken.None);

        var document = JsonNode.Parse(await File.ReadAllTextAsync(destination))!.AsObject();
        Assert.Equal(OcrProbeReport.CurrentSchemaVersion, document["schemaVersion"]!.GetValue<string>());
        Assert.Equal(new[] { destination }, Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task CancellationBeforeTheWriteLeavesThePreviousReportUntouched()
    {
        var destination = Destination(withPreviousReport: true);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            OcrProbe.WriteReportAsync(destination, Report(), cancellation.Token));

        Assert.Equal(PreviousReport, await File.ReadAllTextAsync(destination));
        Assert.Equal(new[] { destination }, Directory.GetFiles(_directory));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellationPartwayThroughTheWriteLeavesNoTruncatedDestination(bool withPreviousReport)
    {
        var destination = Destination(withPreviousReport);
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OcrProbe.WriteAtomicallyAsync(
            destination,
            async (stream, token) =>
            {
                // Half a document reaches the disk, and then Ctrl+C arrives.
                await stream.WriteAsync("{\"schemaVersion\":\"tarkov-comp"u8.ToArray(), token);
                await stream.FlushAsync(token);
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            },
            cancellation.Token));

        if (withPreviousReport)
        {
            Assert.Equal(PreviousReport, await File.ReadAllTextAsync(destination));
            Assert.Equal(new[] { destination }, Directory.GetFiles(_directory));
        }
        else
        {
            Assert.False(File.Exists(destination));
            Assert.Empty(Directory.GetFiles(_directory));
        }
    }

    private string Destination(bool withPreviousReport)
    {
        var destination = Path.Combine(_directory, "report.json");
        if (withPreviousReport)
        {
            File.WriteAllText(destination, PreviousReport);
        }

        return destination;
    }

    private static OcrProbeReport Report()
    {
        var bounds = new PixelRect(0, 0, 640, 120);
        return new OcrProbeReport(
            OcrProbeReport.CurrentSchemaVersion,
            "cells",
            new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero),
            new(640, 120, new DateTimeOffset(2026, 9, 15, 11, 59, 59, TimeSpan.Zero)),
            bounds,
            null,
            [],
            []);
    }
}
