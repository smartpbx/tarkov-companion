using TarkovCompanion.App.Services.V2.Capture;
using TarkovCompanion.App.ViewModels.V2.LootScan;
using TarkovCompanion.App.ViewModels.V2.Shell;
using TarkovCompanion.Application.Services.LootScan;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Recognition.Grid;

namespace TarkovCompanion.V2RenderPreview;

/// <summary>Shows the same fixture midway through matching or after in-place reconciliation.</summary>
internal static class LootProgressDemo
{
    internal static void Show(V2ShellViewModel shell, LootScanResult final, bool complete)
    {
        var started = new LootScanRecognitionStarted(
            final.CaptureSessionId,
            final.ArtifactId,
            final.CorrelationId,
            final.DecodeRevision,
            final.Context,
            final.SourceContentSha256,
            final.EvaluatedUtc.AddMilliseconds(-700));
        var viewModel = LootScanViewModel.CreateProgress(started);
        shell.ShowLootScanProgress(viewModel);

        // Completion order is intentionally not planner order. The final render therefore also
        // exercises row moves without replacing the row objects the mid-scan render showed.
        foreach (var decision in final.Decisions.Take(complete ? final.Decisions.Count : 4).Reverse())
        {
            var field = decision.Item.Bounds is not null
                ? decision.Item
                : new EvidencedValue<RecognizedItem>(
                    decision.Item.FieldId,
                    decision.Item.Value,
                    decision.Item.Status,
                    decision.Item.Provenance,
                    new EvidenceRegion(
                        decision.SourceAnchor.Column * 64,
                        decision.SourceAnchor.Row * 64,
                        64,
                        64,
                        EvidenceCoordinateSpace.SourcePixels),
                    decision.Item.Candidates,
                    decision.Item.Corrections);
            viewModel.AddPending(new(
                final.CaptureSessionId,
                final.ArtifactId,
                final.CorrelationId,
                final.DecodeRevision,
                new GridCellObservation(
                    $"preview-{decision.SourceAnchor.Row}-{decision.SourceAnchor.Column}",
                    decision.SourceAnchor,
                    field)));
        }

        if (complete)
        {
            shell.ApplyLootScanResult(viewModel, final);
        }
    }
}
