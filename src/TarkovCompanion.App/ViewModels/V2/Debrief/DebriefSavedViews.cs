using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using TarkovCompanion.Application.Services.Workspaces;

namespace TarkovCompanion.App.ViewModels.V2.Debrief;

/// <summary>A named Debrief search and filter set.</summary>
public sealed record DebriefSavedView(
    string Name,
    string SearchText,
    string? MapId,
    DebriefOutcomeFilter Outcome,
    DebriefSideFilter Side,
    DateTimeOffset? DateFrom,
    DateTimeOffset? DateTo,
    string? Tag);

/// <summary>Stores a few named views in the same bounded preference file as the other workspace choices.</summary>
/// <remarks>
/// One fixed slot per view keeps each workspace-layout value below that store's 256-character
/// ceiling. Empty slots are reusable, so adding and deleting views cannot consume its 64-key cap.
/// </remarks>
internal sealed class DebriefSavedViewStore(IWorkspaceLayoutStore? store)
{
    public const int MaximumViews = 6;
    public const int MaximumNameLength = 32;
    public const int MaximumSearchLength = 48;

    private const int SchemaVersion = 1;
    private const int MaximumStoredLength = 256;
    private const string KeyPrefix = "debrief.saved-view.";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public IReadOnlyList<DebriefSavedView> Load() =>
        [
            .. Enumerable.Range(0, MaximumViews)
                .Select(Read)
                .OfType<DebriefSavedView>()
                .OrderBy(view => view.Name, StringComparer.CurrentCultureIgnoreCase),
        ];

    public bool Save(DebriefSavedView view)
    {
        if (store is null || Normalize(view) is not { } normalized)
        {
            return false;
        }

        var occupied = Enumerable.Range(0, MaximumViews)
            .Select(index => (Index: index, View: Read(index)))
            .ToArray();
        var slot = occupied.FirstOrDefault(entry => Same(entry.View?.Name, normalized.Name));
        if (slot.View is null)
        {
            slot = occupied.FirstOrDefault(entry => entry.View is null);
            if (slot.View is not null || occupied.All(entry => entry.View is not null))
            {
                return false;
            }
        }

        var document = StoredView.Create(normalized);
        var json = JsonSerializer.Serialize(document, JsonOptions);
        if (json.Length > MaximumStoredLength)
        {
            return false;
        }

        store.Set(Key(slot.Index), json);
        return true;
    }

    public bool Delete(string? name)
    {
        if (store is null || string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        for (var index = 0; index < MaximumViews; index++)
        {
            if (Same(Read(index)?.Name, name))
            {
                store.Set(Key(index), string.Empty);
                return true;
            }
        }

        return false;
    }

    public static DebriefSavedView? Normalize(DebriefSavedView view)
    {
        var name = view.Name.Trim();
        var search = view.SearchText.Trim();
        var map = view.MapId?.Trim();
        var tag = DebriefRaidTags.Normalize(view.Tag);
        if (name.Length is 0 or > MaximumNameLength
            || name.Any(char.IsControl)
            || search.Length > MaximumSearchLength
            || search.Any(char.IsControl)
            || map?.Length > 48)
        {
            return null;
        }

        return view with
        {
            Name = name,
            SearchText = search,
            MapId = string.IsNullOrEmpty(map) ? null : map,
            DateFrom = DateOnly(view.DateFrom),
            DateTo = DateOnly(view.DateTo),
            Tag = tag,
        };
    }

    private DebriefSavedView? Read(int index)
    {
        try
        {
            var json = store?.Get(Key(index));
            if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumStoredLength)
            {
                return null;
            }

            var document = JsonSerializer.Deserialize<StoredView>(json, JsonOptions);
            return document?.Version == SchemaVersion ? Normalize(document.ToView()) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Same(string? left, string? right) =>
        left is not null && right is not null && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string Key(int index) => $"{KeyPrefix}{index}";

    private static DateTimeOffset? DateOnly(DateTimeOffset? value) => value is { } date
        ? new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, TimeSpan.Zero)
        : null;

    private sealed record StoredView(
        [property: JsonPropertyName("v")] int Version,
        [property: JsonPropertyName("n")] string Name,
        [property: JsonPropertyName("q")] string? Search,
        [property: JsonPropertyName("m")] string? Map,
        [property: JsonPropertyName("o")] int Outcome,
        [property: JsonPropertyName("s")] int Side,
        [property: JsonPropertyName("f")] string? From,
        [property: JsonPropertyName("t")] string? To,
        [property: JsonPropertyName("g")] string? Tag)
    {
        public static StoredView Create(DebriefSavedView view) => new(
            SchemaVersion,
            view.Name,
            string.IsNullOrEmpty(view.SearchText) ? null : view.SearchText,
            view.MapId,
            (int)view.Outcome,
            (int)view.Side,
            Format(view.DateFrom),
            Format(view.DateTo),
            view.Tag);

        public DebriefSavedView ToView() => new(
            Name,
            Search ?? string.Empty,
            Map,
            Enum.IsDefined((DebriefOutcomeFilter)Outcome) ? (DebriefOutcomeFilter)Outcome : DebriefOutcomeFilter.Any,
            Enum.IsDefined((DebriefSideFilter)Side) ? (DebriefSideFilter)Side : DebriefSideFilter.Any,
            Parse(From),
            Parse(To),
            Tag);

        private static string? Format(DateTimeOffset? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        private static DateTimeOffset? Parse(string? value) =>
            DateTimeOffset.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed
                : null;
    }
}
