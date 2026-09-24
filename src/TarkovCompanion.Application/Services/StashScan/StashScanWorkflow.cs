using System.Text.Json;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Inventory;
using TarkovCompanion.Core.Domain.Stash;

namespace TarkovCompanion.Application.Services.StashScan;

/// <summary>
/// One application action accepts the ordered, reviewed capture batch, assembles it, and commits
/// the pixel-free result. Capture bytes and process-local content digests do not cross the store.
/// </summary>
public sealed class StashScanWorkflow(
    StashScanAssembler assembler,
    IStashSnapshotStore snapshotStore,
    StashSnapshotComparer comparer,
    IStashReviewCommandSink? reviewCommands = null)
{
    /// <summary>
    /// Raised after a snapshot is stored, on whatever thread saved it. A capture saves from the
    /// capture pipeline, not from the Stash page, and the page only re-read its list when it was
    /// navigated to, so a scan taken with the page open did not appear until the player left it.
    /// </summary>
    public event EventHandler<StashSnapshotRecord>? SnapshotSaved;

    public async Task<StashScanAssemblyResult> CompleteAsync(
        StashScanAssemblyRequest request,
        Guid durableSnapshotId,
        bool makeCurrent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var assembled = assembler.Assemble(request, cancellationToken);
        var record = new StashSnapshotRecord(
            durableSnapshotId,
            assembled.ProfileScope,
            assembled.DataSnapshotId,
            request.AssembledUtc,
            makeCurrent,
            assembled.Recognition);
        await snapshotStore.SaveAsync(record, cancellationToken).ConfigureAwait(false);
        SnapshotSaved?.Invoke(this, record);
        return assembled;
    }

    public async Task<StashSnapshotComparison?> CompareCurrentToAsync(
        InventoryProfileScope scope,
        Guid previousSnapshotId,
        DateTimeOffset comparedUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var previous = await snapshotStore.ReadAsync(scope, previousSnapshotId, cancellationToken)
            .ConfigureAwait(false);
        var current = await snapshotStore.ReadCurrentAsync(scope, cancellationToken).ConfigureAwait(false);
        return previous is null || current is null
            ? null
            : comparer.Compare(previous.Recognition, current.Recognition, comparedUtc);
    }

    public Task<StashSnapshotDeleteResult> DeleteAsync(
        InventoryProfileScope scope,
        Guid snapshotId,
        CancellationToken cancellationToken) =>
        snapshotStore.DeleteAsync(scope, snapshotId, cancellationToken);

    public Task<StashSnapshotRetentionResult> ApplyRetentionAsync(
        InventoryProfileScope scope,
        DateTimeOffset retainFromUtc,
        bool dryRun,
        CancellationToken cancellationToken) =>
        snapshotStore.ApplyRetentionAsync(scope, retainFromUtc, dryRun, cancellationToken);

    public async Task<string?> ExportAsync(
        InventoryProfileScope scope,
        Guid snapshotId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var snapshot = await snapshotStore.ReadAsync(scope, snapshotId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return null;
        }

        var export = new StashSnapshotExportDocument(
            "tarkov-companion.stash-snapshot.v2",
            snapshot.SnapshotId,
            snapshot.ProfileScope,
            snapshot.DataSnapshotId,
            snapshot.RecordedUtc,
            snapshot.Recognition);
        return JsonSerializer.Serialize(export, V2ContractJson.Options);
    }

    public Task ReviewAsync(StashReviewCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return reviewCommands is null
            ? throw new InvalidOperationException("Stash review persistence is not composed.")
            : reviewCommands.AppendAsync(command, cancellationToken);
    }

    private sealed record StashSnapshotExportDocument(
        string Schema,
        Guid SnapshotId,
        InventoryProfileScope ProfileScope,
        string DataSnapshotId,
        DateTimeOffset RecordedUtc,
        RecognitionResultEnvelope<StashRecognition> Recognition);
}
