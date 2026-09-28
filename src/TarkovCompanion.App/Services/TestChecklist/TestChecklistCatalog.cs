using System.Text.Json;
using System.Text.RegularExpressions;

namespace TarkovCompanion.App.Services.TestChecklist;

/// <summary>One thing to try by hand, as <c>Assets/TestChecklist/NN-*.json</c> describes it (schema version 1).</summary>
/// <param name="Id">Stable, lowercase, dot-separated; a result is keyed by it, so it is never reused.</param>
/// <param name="Area">The heading it is grouped under.</param>
/// <param name="Feature">Its short name.</param>
/// <param name="Needs">What the tester needs to hand (game, in-raid, second-pc, …), from <see cref="TestChecklistCatalog.KnownNeeds"/>.</param>
/// <param name="Steps">What to do, in order.</param>
/// <param name="Expect">What the tester should see.</param>
/// <param name="GoTo">A shell address (<c>#/raid</c>) the Go there button opens, or null.</param>
/// <param name="Flag">The feature flag it sits behind, or null.</param>
/// <param name="Refs">PRs or issues, for whoever reads a report.</param>
public sealed record TestChecklistItem(
    string Id,
    string Area,
    string Feature,
    IReadOnlyList<string> Needs,
    IReadOnlyList<string> Steps,
    string Expect,
    string? GoTo,
    string? Flag,
    IReadOnlyList<string> Refs);

/// <summary>An entry a checklist file carried that could not be used, and why.</summary>
public sealed record TestChecklistProblem(string File, string Where, string Reason)
{
    public override string ToString() => $"{File} {Where}: {Reason}";
}

/// <summary>Every item the checklist files carried, in file order, and what could not be read.</summary>
/// <remarks>
/// Two workers write the item files in parallel, so the reader cannot assume they agree. A bad entry
/// is skipped and named in <see cref="Problems"/>, and the page says how many; a unit test reads the
/// shipped files and fails on any problem at all, so a broken file never reaches a player unnoticed.
/// </remarks>
public sealed class TestChecklistCatalog
{
    public const int SchemaVersion = 1;

    /// <summary>What an item may say it needs. Anything else is a typo and is refused.</summary>
    public static IReadOnlySet<string> KnownNeeds { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "game", "in-raid", "pmc", "scav", "squad", "tablet", "relay",
        "stash-screenshot", "loot-screenshot", "flea-screenshot", "second-pc", "windows",
    };

    private static readonly Regex IdShape = new("^[a-z0-9]+(?:[.-][a-z0-9]+)*$", RegexOptions.CultureInvariant);

    private TestChecklistCatalog(IReadOnlyList<TestChecklistItem> items, IReadOnlyList<TestChecklistProblem> problems)
    {
        Items = items;
        Problems = problems;
    }

    public static TestChecklistCatalog Empty { get; } = new([], []);

    /// <summary>The folder itself could not be read.</summary>
    public static TestChecklistCatalog Unreadable(string reason) => new([], [new("Assets/TestChecklist", "(folder)", reason)]);

    public IReadOnlyList<TestChecklistItem> Items { get; }

    public IReadOnlyList<TestChecklistProblem> Problems { get; }

    /// <summary>Reads the files sorted by name (ordinal), concatenating their items; an id seen before is refused.</summary>
    public static TestChecklistCatalog Parse(IEnumerable<(string Name, string Json)> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var items = new List<TestChecklistItem>();
        var problems = new List<TestChecklistProblem>();
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, json) in files.OrderBy(file => file.Name, StringComparer.Ordinal))
        {
            ReadFile(name, json, items, problems, seen);
        }

        return new(items, problems);
    }

    private static void ReadFile(
        string name,
        string json,
        List<TestChecklistItem> items,
        List<TestChecklistProblem> problems,
        Dictionary<string, string> seen)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException exception)
        {
            problems.Add(new(name, "(file)", $"not JSON: {exception.Message}"));
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                problems.Add(new(name, "(file)", "the file is not an object"));
                return;
            }

            if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var number) || number != SchemaVersion)
            {
                problems.Add(new(name, "(file)", $"\"version\" must be {SchemaVersion}"));
                return;
            }

            if (!root.TryGetProperty("items", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                problems.Add(new(name, "(file)", "\"items\" must be an array"));
                return;
            }

            var index = 0;
            foreach (var element in list.EnumerateArray())
            {
                var where = $"item {index}";
                index++;
                var item = ReadItem(element, out var reason);
                if (item is null)
                {
                    problems.Add(new(name, where, reason!));
                    continue;
                }

                where = $"'{item.Id}'";
                if (seen.TryGetValue(item.Id, out var first))
                {
                    problems.Add(new(name, where, $"duplicate id, first in {first}"));
                    continue;
                }

                seen[item.Id] = name;
                items.Add(item);
            }
        }
    }

    private static TestChecklistItem? ReadItem(JsonElement element, out string? reason)
    {
        reason = null;
        if (element.ValueKind != JsonValueKind.Object)
        {
            reason = "not an object";
            return null;
        }

        var id = Text(element, "id");
        if (id is null || !IdShape.IsMatch(id))
        {
            reason = "\"id\" must be lowercase words joined by dots or hyphens";
            return null;
        }

        var area = Text(element, "area");
        var feature = Text(element, "feature");
        var expect = Text(element, "expect");
        if (area is null || feature is null || expect is null)
        {
            reason = $"'{id}' needs \"area\", \"feature\" and \"expect\"";
            return null;
        }

        var steps = TextList(element, "steps");
        if (steps is null || steps.Count == 0)
        {
            reason = $"'{id}' needs \"steps\": at least one non-empty string";
            return null;
        }

        var needs = element.TryGetProperty("needs", out _) ? TextList(element, "needs") : [];
        if (needs is null)
        {
            reason = $"'{id}': \"needs\" must be a list of strings";
            return null;
        }

        if (needs.FirstOrDefault(need => !KnownNeeds.Contains(need)) is { } unknown)
        {
            reason = $"'{id}': unknown need '{unknown}'";
            return null;
        }

        var goTo = Optional(element, "goTo", out var badGoTo);
        if (badGoTo || (goTo is not null && !goTo.StartsWith("#/", StringComparison.Ordinal)))
        {
            reason = $"'{id}': \"goTo\" must be an address such as #/raid";
            return null;
        }

        var flag = Optional(element, "flag", out var badFlag);
        if (badFlag || (flag is not null && !TarkovCompanion.Core.Features.Flag.All.Any(known => known.Key == flag)))
        {
            reason = $"'{id}': unknown flag '{flag}'";
            return null;
        }

        var refs = element.TryGetProperty("refs", out _) ? TextList(element, "refs") : [];
        if (refs is null)
        {
            reason = $"'{id}': \"refs\" must be a list of strings";
            return null;
        }

        return new(id, area, feature, needs, steps, expect, goTo, flag, refs);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { } text && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

    /// <summary>Null when absent or JSON null; <paramref name="bad"/> when present and not a non-empty string.</summary>
    private static string? Optional(JsonElement element, string name, out bool bad)
    {
        bad = false;
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var text = Text(element, name);
        bad = text is null;
        return text;
    }

    private static List<string>? TextList(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<string>();
        foreach (var entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(entry.GetString()))
            {
                return null;
            }

            list.Add(entry.GetString()!.Trim());
        }

        return list;
    }
}

/// <summary>Reads every <c>Assets/TestChecklist/*.json</c> embedded in the application.</summary>
public static class TestChecklistAssets
{
    public static readonly Uri Folder = new("avares://TarkovCompanion/Assets/TestChecklist/");

    /// <summary>The shipped items; a folder that cannot be listed at all is one problem, not a crash.</summary>
    public static TestChecklistCatalog Load()
    {
        var files = new List<(string, string)>();
        try
        {
            foreach (var asset in Avalonia.Platform.AssetLoader.GetAssets(Folder, null))
            {
                var name = asset.AbsolutePath[(asset.AbsolutePath.LastIndexOf('/') + 1)..];
                if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using var stream = Avalonia.Platform.AssetLoader.Open(asset);
                using var reader = new StreamReader(stream);
                files.Add((name, reader.ReadToEnd()));
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // No Avalonia platform (a test that builds the shell without one), or a damaged package:
            // the page says it could not read its items rather than taking Setup down with it.
            return TestChecklistCatalog.Unreadable(exception.GetType().Name);
        }

        return TestChecklistCatalog.Parse(files);
    }
}
