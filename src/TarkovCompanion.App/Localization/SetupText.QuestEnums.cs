using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.App.Localization;

/// <summary>Words for enum values Setup used to print by name (#314): quest import rows, data state.</summary>
public static partial class SetupText
{
    public static string QuestEntityKind(QuestProgressEntityKind kind) => EnumWord("Setup.QuestEnum.Entity", kind);

    /// <summary>A kind stored as its name (import history keeps the name, not the number).</summary>
    public static string QuestEntityKind(string stored) =>
        Enum.TryParse<QuestProgressEntityKind>(stored, ignoreCase: false, out var kind) && Enum.IsDefined(kind)
            ? QuestEntityKind(kind)
            : stored;

    public static string QuestResolution(QuestImportResolution resolution) => EnumWord("Setup.QuestEnum.Resolution", resolution);

    public static string QuestTaskState(RecordedTaskState state) => EnumWord("Setup.QuestEnum.TaskState", state);

    public static string QuestObjectiveState(RecordedObjectiveState state) => EnumWord("Setup.QuestEnum.ObjectiveState", state);

    public static string QuestPinKind(QuestPinTargetKind kind) => EnumWord("Setup.QuestEnum.PinKind", kind);

    /// <summary>"Current", "Cached", "Refreshing"… for the data status line.</summary>
    public static string DataAvailabilityName(DataAvailability availability) => EnumWord("Setup.DataAvailability", availability);

    public static string QuestsEntity(string kind, object? id) => UiText.Format("Setup.QuestEnum.EntityLine", kind, id);
    public static string QuestsHistoryEntity(string kind, object? id) => UiText.Format("Setup.QuestEnum.HistoryEntity", kind, id);

    public static string FoldersLooking => UiText.Get("Setup.Folders.Looking");
    public static string FoldersBoth(object? screenshots, object? logs) => UiText.Format("Setup.Folders.Both", screenshots, logs);
    public static string FoldersNoLogs(object? screenshots) => UiText.Format("Setup.Folders.NoLogs", screenshots);
    public static string FoldersNoScreenshots(object? logs) => UiText.Format("Setup.Folders.NoScreenshots", logs);
    public static string FoldersNothing => UiText.Get("Setup.Folders.Nothing");

    /// <summary>The word for a defined value; an undefined one (a newer build's) shows its number rather than throwing.</summary>
    private static string EnumWord<T>(string prefix, T value)
        where T : struct, Enum =>
        Enum.IsDefined(value) ? UiText.Get($"{prefix}.{value}") : value.ToString();
}
