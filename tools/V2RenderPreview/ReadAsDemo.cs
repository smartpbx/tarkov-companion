using TarkovCompanion.App.Services.V2.Capture;
using TarkovCompanion.App.ViewModels.V2.Shell;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;

namespace TarkovCompanion.V2RenderPreview;

/// <summary>
/// #287: the Loot page's retention chip and Read as… after a real --capture-image, and the
/// capture panel's "not supported yet" answer for a screen nothing reads.
/// </summary>
internal static class ReadAsDemo
{
    /// <summary>
    /// --read-as-open opens the menu on whatever result the capture left; --read-as &lt;intent&gt;
    /// presses one of its buttons and waits for the second reading to finish.
    /// </summary>
    internal static void Run(V2ShellViewModel shell, ICaptureSessionService sessions, string? readAs, bool open, Action<int> pump)
    {
        var source = shell.LootScanResult?.Source ?? shell.CaptureReviewSource;
        Console.WriteLine($"Scan source: {(source is null ? "none" : $"{source.RetentionLabel} | options {string.Join(",", source.ReadAsOptions.Select(option => option.Label))}")}");
        if (source is null)
        {
            return;
        }

        source.IsReadAsOpen = open || readAs is not null;
        if (readAs is not null)
        {
            var intent = Enum.Parse<ScanIntent>(readAs, ignoreCase: true);
            var before = sessions.Snapshot.Sessions.Length;
            source.ReadAsOptions.Single(option => option.Intent == intent).Command.Execute(null);
            for (var turn = 0; turn < 2400 && !(sessions.Snapshot.Sessions.Length > before && sessions.Snapshot.Sessions[^1].IsTerminal); turn++)
            {
                pump(1);
                Thread.Sleep(25);
            }

            pump(40);
            Console.WriteLine($"Read as {intent}: {source.Status} | route {shell.Router.CurrentAddress}");
            foreach (var notice in sessions.Snapshot.Notices.TakeLast(6))
            {
                Console.WriteLine($"Capture notice: {notice.Kind} {notice.Code}");
            }
        }

        pump(20);
    }

    /// <summary>
    /// --capture-unsupported health|extracts: the handoff the composite routes those screens to,
    /// given the request the coordinator would hand it. Recognising the HEALTH tab needs Windows
    /// OCR, which this host has not got, so the recognition half is covered by unit tests.
    /// </summary>
    internal static void Unsupported(UnsupportedScreenHandoff handoff, string which, Action<int> pump)
    {
        var intent = which.StartsWith("extract", StringComparison.OrdinalIgnoreCase)
            ? ScanIntent.ExtractsAndMap
            : ScanIntent.HealthAndCharacter;
        var now = DateTimeOffset.UtcNow;
        var session = new CaptureSessionId(Guid.Parse("30000000-0000-0000-0000-000000000288"));
        var correction = new CaptureCorrection(CaptureReviewAction.UseDetected, intent, null, 0, now, "render");
        var request = new CaptureHandoffRequest(
            session,
            "capture-unsupported-demo",
            new CaptureAnalysis(new string('0', 64), null, false, true, null, new(0.86)),
            CaptureContextMetadata.Empty,
            CaptureCorrelationId.New(),
            CaptureSourceKind.GameWrittenScreenshot,
            now,
            now,
            null,
            CaptureDeliveryKind.WatchedFile,
            new EvidenceProvenance(EvidenceSourceClass.GameWrittenScreenshot, "render", now, EvidenceConfidence.Certain, new("render", "1")),
            0,
            CaptureReviewAction.UseDetected,
            intent,
            correction);
        handoff.AcceptAsync(request, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        pump(40);
    }
}
