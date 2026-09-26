using TarkovCompanion.Application.Services.Readiness;
using TarkovCompanion.Core.Domain.Situations;

namespace TarkovCompanion.App.Localization;

/// <summary>[#712 1-13] The Raid page's readiness strip (#314 recipe: Readiness.* in the tables).</summary>
public static class ReadinessText
{
    public static string StripName => UiText.Get("Readiness.StripName");
    public static string Details => UiText.Get("Readiness.Details");

    public static string Item(ReadinessItemKind kind) => UiText.Get(kind switch
    {
        ReadinessItemKind.GameLogs => "Readiness.Item.GameLogs",
        ReadinessItemKind.Screenshots => "Readiness.Item.Screenshots",
        ReadinessItemKind.GameData => "Readiness.Item.GameData",
        ReadinessItemKind.Squad => "Readiness.Item.Squad",
        ReadinessItemKind.GameFormat => "Readiness.Item.GameFormat",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    });

    /// <summary>"✓", "not found", "downloading…": the few words after an item's name.</summary>
    public static string State(ReadinessItem item, FormatHealthReport? format)
    {
        ArgumentNullException.ThrowIfNull(item);
        var data = item.Kind == ReadinessItemKind.GameData;
        return item.State switch
        {
            ReadinessItemState.Ready => UiText.Get("Readiness.State.Ready"),
            ReadinessItemState.Checking => UiText.Get(data ? "Readiness.State.Downloading" : "Readiness.State.Looking"),
            ReadinessItemState.Missing => UiText.Get(data ? "Readiness.State.NotDownloaded" : "Readiness.State.NotFound"),
            ReadinessItemState.LocalOnly => UiText.Get("Readiness.State.LocalOnly"),
            ReadinessItemState.Optional => UiText.Get("Readiness.State.Optional"),
            ReadinessItemState.Failed when item.Kind == ReadinessItemKind.GameFormat =>
                format?.Sources.FirstOrDefault(source => source.ChangedAfterUpdate) is { GameVersion: { } version }
                    ? UiText.Format("Readiness.State.FormatChangedAfter", version)
                    : UiText.Get("Readiness.State.FormatChanged"),
            ReadinessItemState.Failed => UiText.Get("Readiness.State.DownloadFailed"),
            _ => throw new ArgumentOutOfRangeException(nameof(item), item.State, null),
        };
    }

    public static string? Fix(ReadinessFix fix) => fix switch
    {
        ReadinessFix.None => null,
        ReadinessFix.ChooseLogFolder or ReadinessFix.ChooseScreenshotFolder => UiText.Get("Readiness.Fix.ChooseFolder"),
        ReadinessFix.RetryData => UiText.Get("Readiness.Fix.Retry"),
        ReadinessFix.OpenDataNetwork => UiText.Get("Readiness.Fix.OpenDataNetwork"),
        ReadinessFix.OpenSquad => UiText.Get("Readiness.Fix.OpenSquad"),
        ReadinessFix.OpenDetails or ReadinessFix.OpenFormatHealth => UiText.Get("Readiness.Fix.OpenDetails"),
        _ => throw new ArgumentOutOfRangeException(nameof(fix), fix, null),
    };

    public static string PickerTitle(ReadinessItemKind kind) => UiText.Get(kind == ReadinessItemKind.GameLogs
        ? "Readiness.Picker.Logs"
        : "Readiness.Picker.Screenshots");
}
