using System.Globalization;
using System.Text;
using System.Text.Json;
using TarkovCompanion.Application.Services.StashScan;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Stash;

namespace TarkovCompanion.App.ViewModels.V2.StashScan;

/// <summary>Player-readable exports of the one stitched stash, never the source screenshots.</summary>
public static class StashSnapshotExport
{
    public static string Csv(StashSnapshotRecord snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var reconstruction = new StashReconstructionProjector().Project(Recognition(snapshot));
        var text = new StringBuilder();
        text.AppendLine("snapshot_id,recorded_local,container,row,column,width,height,item_id,name,quantity,status");
        foreach (var tile in reconstruction.Containers
                     .OrderBy(container => container.ContainerPath, StringComparer.Ordinal)
                     .SelectMany(container => container.Tiles.OrderBy(tile => tile.Row).ThenBy(tile => tile.Column)))
        {
            text.Append(Escape(snapshot.SnapshotId.ToString("D"))).Append(',')
                .Append(Escape(LocalTime.Iso(snapshot.RecordedUtc))).Append(',')
                .Append(Escape(tile.ContainerPath)).Append(',')
                .Append(tile.Row.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(tile.Column.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(tile.Width.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(tile.Height.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(Escape(tile.ItemId)).Append(',')
                .Append(Escape(tile.DisplayName ?? tile.CandidateNames.FirstOrDefault())).Append(',')
                .Append(tile.Quantity?.ToString(CultureInfo.InvariantCulture)).Append(',')
                .AppendLine(tile.IsKnown ? "known" : "unknown");
        }

        return text.ToString();
    }

    public static string Json(StashSnapshotRecord snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var reconstruction = new StashReconstructionProjector().Project(Recognition(snapshot));
        var document = new ExportDocument(
            "tarkov-companion.stash-snapshot.v1",
            snapshot.SnapshotId,
            snapshot.ProfileScope.ProfileId,
            snapshot.ProfileScope.Generation,
            snapshot.ProfileScope.GameMode,
            snapshot.DataSnapshotId,
            LocalTime.Iso(snapshot.RecordedUtc),
            LocalTime.Offset(snapshot.RecordedUtc),
            reconstruction.UnplacedRegions,
            reconstruction.Containers
                .OrderBy(container => container.ContainerPath, StringComparer.Ordinal)
                .SelectMany(container => container.Tiles.OrderBy(tile => tile.Row).ThenBy(tile => tile.Column))
                .Select(tile => new ExportItem(
                    tile.ContainerPath,
                    tile.Row,
                    tile.Column,
                    tile.Width,
                    tile.Height,
                    tile.ItemId,
                    tile.DisplayName,
                    tile.Quantity,
                    tile.IsKnown ? "known" : "unknown",
                    tile.CandidateNames))
                .ToArray());
        return JsonSerializer.Serialize(document, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        });
    }

    private static StashRecognition Recognition(StashSnapshotRecord snapshot) =>
        snapshot.Recognition.Result.Value
        ?? throw new InvalidDataException("The stash snapshot has no recognition payload.");

    private static string Escape(string? value)
    {
        var text = value ?? string.Empty;
        return text.IndexOfAny([',', '"', '\r', '\n']) < 0
            ? text
            : $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private sealed record ExportDocument(
        string Schema,
        Guid SnapshotId,
        Guid ProfileId,
        string ProfileGeneration,
        string GameMode,
        string DataSnapshotId,
        string RecordedLocal,
        string TimeZoneOffset,
        int UnplacedScreenshots,
        IReadOnlyList<ExportItem> Items);

    private sealed record ExportItem(
        string Container,
        int Row,
        int Column,
        int Width,
        int Height,
        string? ItemId,
        string? Name,
        int? Quantity,
        string Status,
        IReadOnlyList<string> Candidates);
}
