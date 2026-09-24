using System.Text.Json.Nodes;

namespace TarkovCompanion.UnitTests.Compatibility;

/// <summary>
/// Wire messages that released builds sent or accepted, kept as files beside these tests.
/// </summary>
/// <remarks>
/// [#294] Players update at different times and the relay is redeployed on its own, so every pairing
/// of an older and a newer build is live at some point. Each fixture is named for the commit whose
/// source it was derived from (v2-rough-1 and v2-rough-11 are tags; 53a3b743 is the relay build
/// deployed before the #289 Ready check). Ids and names in them are synthetic.
/// </remarks>
internal static class CompatibilityFixtures
{
    public static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Compatibility", "Fixtures", name));

    public static JsonNode Node(string name) => JsonNode.Parse(Read(name))!;

    /// <summary>
    /// Every path in <paramref name="paths"/> that <paramref name="root"/> does not carry.
    /// </summary>
    /// <remarks>
    /// A path is dot-separated; a segment ending in <c>[]</c> is an array whose every element must
    /// carry the rest, and which must not be empty, since an empty array proves nothing. The last
    /// segment has to be present but may be null; a segment before it has to be a value, because the
    /// page reading it dereferences it.
    /// </remarks>
    public static IReadOnlyList<string> Missing(JsonNode root, IEnumerable<string> paths) =>
        [.. paths.Where(path => !Carries(root, path.Split('.'), 0))];

    private static bool Carries(JsonNode? node, string[] segments, int index)
    {
        if (index == segments.Length)
        {
            return true;
        }

        if (node is not JsonObject target)
        {
            return false;
        }

        var segment = segments[index];
        var isArray = segment.EndsWith("[]", StringComparison.Ordinal);
        var name = isArray ? segment[..^2] : segment;
        if (!target.TryGetPropertyValue(name, out var value))
        {
            return false;
        }

        if (isArray)
        {
            return value is JsonArray { Count: > 0 } items &&
                items.All(item => Carries(item, segments, index + 1));
        }

        return index == segments.Length - 1 || Carries(value, segments, index + 1);
    }
}
