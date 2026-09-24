using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TarkovCompanion.Core.Common;

namespace TarkovCompanion.Core.Domain.Quests;

public static class ProjectQuestProgressFormat
{
    public const string Identifier = "tarkov-companion.quest-progress";
    public const int Version = 2;
}

public sealed record ProjectQuestProgressTask(string TaskId, RecordedTaskState State);

public sealed record ProjectQuestProgressObjective(
    string ObjectiveId,
    RecordedObjectiveState State,
    decimal? Count);

public sealed record ProjectQuestProgressHolding(string ItemId, bool FoundInRaid, int Count);

public sealed record ProjectQuestProgressPin(
    QuestPinTargetKind TargetKind,
    string TargetId,
    int SortOrder,
    string? Note);

public sealed record ProjectQuestProgressProfile(
    Guid ProfileId,
    string ProfileName,
    GameMode GameMode,
    string ProfileGeneration,
    IReadOnlyList<ProjectQuestProgressTask> Tasks,
    IReadOnlyList<ProjectQuestProgressObjective> Objectives,
    IReadOnlyList<ProjectQuestProgressHolding> Holdings,
    IReadOnlyList<ProjectQuestProgressPin> Pins);

public sealed record ProjectQuestProgressDocument(
    string FormatId,
    int FormatVersion,
    string AppVersion,
    DateTimeOffset ExportedUtc,
    string ProvenanceSummary,
    string PayloadSha256,
    IReadOnlyList<ProjectQuestProgressProfile> Profiles,
    bool IsLegacyProfileSettingsEnvelope = false);

public enum QuestImportClassification
{
    SafeMonotonic,
    Conflict,
    IgnoredUnchanged,
    UnresolvedUnknownId,
    UnresolvedSourceRecord,
}

public enum QuestProgressImportSource
{
    ProjectJsonV2,
    LegacyProfileJsonV1,
    TarkovTracker,
}

public sealed record QuestProgressImportTask(
    string TaskId,
    RecordedTaskState? State,
    string? UnresolvedReason = null);

public sealed record QuestProgressImportObjective(
    string ObjectiveId,
    RecordedObjectiveState? State,
    decimal? Count,
    string? UnresolvedReason = null);

public sealed record QuestProgressImportSnapshot(
    QuestProgressImportSource Source,
    GameMode GameMode,
    string? SourceGeneration,
    string PayloadSha256,
    string SourceVersion,
    DateTimeOffset ObservedUtc,
    string ProvenanceSummary,
    IReadOnlyList<QuestProgressImportTask> Tasks,
    IReadOnlyList<QuestProgressImportObjective> Objectives,
    IReadOnlyList<ProjectQuestProgressHolding> Holdings,
    IReadOnlyList<ProjectQuestProgressPin> Pins);

public enum QuestImportResolution
{
    KeepLocal,
    UseIncoming,
}

public sealed record QuestImportValue(
    RecordedTaskState? TaskState = null,
    RecordedObjectiveState? ObjectiveState = null,
    decimal? ObjectiveCount = null,
    bool? HoldingFoundInRaid = null,
    int? HoldingCount = null,
    QuestPinTargetKind? PinTargetKind = null,
    int? PinSortOrder = null,
    string? PinNote = null);

/// <param name="Reason">Why, in fixed English: it is hashed into the preview and stored with the import.</param>
public sealed record QuestImportProposal(
    string Key,
    QuestProgressEntityKind EntityKind,
    string EntityId,
    string? Subkey,
    QuestImportClassification Classification,
    QuestImportValue? LocalValue,
    QuestImportValue IncomingValue,
    string Reason)
{
    /// <summary>
    /// [#314] <see cref="Reason"/> as a code the App words. Null where the reason came from the
    /// source file's adapter, which is said as written.
    /// </summary>
    public QuestImportReason? ReasonCode { get; init; }
}

/// <summary>[#314] Why an import proposal was classified as it was, as codes the App words.</summary>
[PhraseCodes("Setup.QuestImportReason")]
public enum QuestImportReason
{
    TaskUnreadable,
    TaskUnknown,
    TaskUnchanged,
    TaskConflict,
    TaskPromotion,
    ObjectiveUnreadable,
    ObjectiveUnknown,
    ObjectiveUnchanged,
    ObjectiveConflict,
    ObjectivePromotion,
    HoldingUnknown,
    HoldingUnchanged,
    HoldingLower,
    HoldingNew,
    PinUnknown,
    PinUnchanged,
    PinDiffers,
    PinNew,
}

/// <summary>
/// The fixed English each <see cref="QuestImportReason"/> is stored and hashed as. Never shown
/// from here: the App words the code, and finds the code of a stored reason through <see cref="Find"/>.
/// </summary>
public static class QuestImportReasons
{
    public static string Stored(QuestImportReason reason) => reason switch
    {
        QuestImportReason.TaskUnreadable => "The source task record could not be mapped safely.",
        QuestImportReason.TaskUnknown => "Task id is not present in the selected mode catalog.",
        QuestImportReason.TaskUnchanged => "Incoming task state matches local state; absence elsewhere is not deletion.",
        QuestImportReason.TaskConflict => "Incoming task state conflicts with or regresses stronger local progress.",
        QuestImportReason.TaskPromotion => "Incoming task state is a monotonic promotion.",
        QuestImportReason.ObjectiveUnreadable => "The source objective record could not be mapped safely.",
        QuestImportReason.ObjectiveUnknown => "Objective id is not present in the selected mode catalog.",
        QuestImportReason.ObjectiveUnchanged => "Incoming objective progress matches local progress; absence elsewhere is not deletion.",
        QuestImportReason.ObjectiveConflict => "Incoming objective state or count regresses stronger local progress.",
        QuestImportReason.ObjectivePromotion => "Incoming objective state or count is a monotonic promotion.",
        QuestImportReason.HoldingUnknown => "Holding item id is not present in a quest objective in the selected mode catalog.",
        QuestImportReason.HoldingUnchanged => "Incoming explicit holding matches the local FIR class count.",
        QuestImportReason.HoldingLower => "Incoming explicit holding count is lower than local progress.",
        QuestImportReason.HoldingNew => "Incoming explicit holding is new or increased for this exact FIR class.",
        QuestImportReason.PinUnknown => "Pin target id is not present in the selected mode catalog.",
        QuestImportReason.PinUnchanged => "Incoming pin matches the local pin.",
        QuestImportReason.PinDiffers => "Incoming pin ordering or note differs from the local pin.",
        QuestImportReason.PinNew => "Incoming pin adds a new owned pin.",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };

    /// <summary>The code a stored reason was written from, or null for one an adapter wrote.</summary>
    public static QuestImportReason? Find(string? stored)
    {
        foreach (var reason in Enum.GetValues<QuestImportReason>())
        {
            if (string.Equals(Stored(reason), stored, StringComparison.Ordinal))
            {
                return reason;
            }
        }

        return null;
    }
}

public sealed record QuestProgressImportPreview(
    QuestProfileScope Scope,
    string ProfileName,
    long BaseRevision,
    string PayloadSha256,
    string PreviewSha256,
    string SourceAppVersion,
    DateTimeOffset ExportedUtc,
    string ProvenanceSummary,
    bool IsLegacyProfileSettingsEnvelope,
    IReadOnlyList<QuestImportProposal> Proposals,
    QuestProgressImportSource Source = QuestProgressImportSource.ProjectJsonV2)
{
    public IReadOnlyList<QuestImportProposal> SafeProposals =>
        Proposals.Where(value => value.Classification == QuestImportClassification.SafeMonotonic).ToArray();

    public IReadOnlyList<QuestImportProposal> Conflicts =>
        Proposals.Where(value => value.Classification == QuestImportClassification.Conflict).ToArray();

    public IReadOnlyList<QuestImportProposal> Ignored =>
        Proposals.Where(value => value.Classification == QuestImportClassification.IgnoredUnchanged).ToArray();

    public IReadOnlyList<QuestImportProposal> Unresolved =>
        Proposals.Where(value => value.Classification is
            QuestImportClassification.UnresolvedUnknownId or
            QuestImportClassification.UnresolvedSourceRecord).ToArray();
}

public enum IntegrationSecretKind
{
    TarkovTrackerProgressToken,
    /// <summary>Read-only credential for the private signed release feed.</summary>
    ReleaseFeedReadToken,

    /// <summary>
    /// This desktop's owner session on its group relay, so a restart does not ask for the relay's
    /// admin key again. A session the relay issued and can end, never the admin key itself.
    /// </summary>
    RelayOwnerSession,

    /// <summary>
    /// One paired device's session traffic keys, so a desktop restart can still open that device's
    /// frames. Deleted when the device is revoked.
    /// </summary>
    PairedDeviceSession,
}

public sealed record IntegrationSecretReference(
    IntegrationSecretKind Kind,
    Guid ProfileId,
    GameMode GameMode,
    string ProfileGeneration);

public sealed record QuestImportApplyResult(
    Guid ImportId,
    long Revision,
    bool Changed,
    bool AlreadyApplied,
    int AppliedChangeCount,
    int KeptLocalCount,
    int UnresolvedCount);

public sealed record QuestImportUndoResult(
    Guid ImportId,
    Guid UndoCorrelationId,
    long Revision,
    bool Changed,
    bool AlreadyUndone,
    int RestoredChangeCount);

public sealed record QuestProgressExportResult(string PayloadSha256, int RecordCount);

public static class QuestImportPreviewHash
{
    public static string Compute(QuestProgressImportPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Add(hash, preview.Scope.ProfileId.ToString("D"));
        Add(hash, preview.Scope.GameMode.ToString());
        Add(hash, preview.Scope.Generation);
        Add(hash, preview.ProfileName);
        Add(hash, preview.BaseRevision.ToString(CultureInfo.InvariantCulture));
        Add(hash, preview.PayloadSha256);
        Add(hash, preview.SourceAppVersion);
        Add(hash, preview.ExportedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        Add(hash, preview.ProvenanceSummary);
        Add(hash, preview.IsLegacyProfileSettingsEnvelope ? "1" : "0");
        Add(hash, preview.Source.ToString());
        foreach (var proposal in preview.Proposals.OrderBy(value => value.Key, StringComparer.Ordinal))
        {
            Add(hash, proposal.Key);
            Add(hash, proposal.EntityKind.ToString());
            Add(hash, proposal.EntityId);
            Add(hash, proposal.Subkey);
            Add(hash, proposal.Classification.ToString());
            AddValue(hash, proposal.LocalValue);
            AddValue(hash, proposal.IncomingValue);
            Add(hash, proposal.Reason);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AddValue(IncrementalHash hash, QuestImportValue? value)
    {
        if (value is null)
        {
            Add(hash, null);
            return;
        }

        Add(hash, value.TaskState?.ToString());
        Add(hash, value.ObjectiveState?.ToString());
        Add(hash, value.ObjectiveCount?.ToString(CultureInfo.InvariantCulture));
        Add(hash, value.HoldingFoundInRaid is null ? null : value.HoldingFoundInRaid.Value ? "1" : "0");
        Add(hash, value.HoldingCount?.ToString(CultureInfo.InvariantCulture));
        Add(hash, value.PinTargetKind?.ToString());
        Add(hash, value.PinSortOrder?.ToString(CultureInfo.InvariantCulture));
        Add(hash, value.PinNote);
    }

    private static void Add(IncrementalHash hash, string? value)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        if (value is null)
        {
            BinaryPrimitives.WriteInt32BigEndian(length, -1);
            hash.AppendData(length);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
