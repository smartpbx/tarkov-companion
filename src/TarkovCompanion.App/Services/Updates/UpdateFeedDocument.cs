using System.Text.Json;
using System.Text.RegularExpressions;

namespace TarkovCompanion.App.Services.Updates;

/// <summary>One package the rough channel offers, as its feed describes it.</summary>
/// <param name="PackageId">The installer's pack id.</param>
/// <param name="Version">The build's version, as written in the feed.</param>
/// <param name="IsFull">Whether this is a whole build rather than a difference from an older one.</param>
/// <param name="FileName">The file's name beside the feed. Never a path.</param>
/// <param name="Sha256">The SHA256 the feed promises for the file's bytes, upper-case hex.</param>
/// <param name="Size">The file's length in bytes.</param>
public sealed record UpdateFeedPackage(
    string PackageId,
    string Version,
    bool IsFull,
    string FileName,
    string Sha256,
    long Size);

/// <summary>The feed could not be trusted enough to read a package out of it.</summary>
public sealed class UpdateFeedException(string message) : Exception(message);

/// <summary>
/// The rough channel's feed: the list of packages and the hash each one must have.
/// </summary>
/// <remarks>
/// The document is the one the packaging tool already writes (<c>releases.win.json</c>), so
/// publishing a build is copying its output folder and nothing has to be generated on the relay.
///
/// It is read here a second time, separately from the updater library's own reader, for one
/// reason: the library treats SHA256 as optional and falls back to SHA1 when a feed leaves it
/// out. This channel has no signature, so the hash in the feed is the only statement about what
/// the bytes should be. A feed that omits it is refused rather than quietly checked more weakly.
///
/// Unknown fields are ignored, because the packaging tool adds them between versions. A missing
/// or malformed required field fails the whole document: half a feed is not a feed.
/// </remarks>
public sealed partial class UpdateFeedDocument
{
    /// <summary>A feed is a few hundred bytes per package. Anything near this is not a feed.</summary>
    public const int MaximumLength = 1024 * 1024;

    private UpdateFeedDocument(IReadOnlyList<UpdateFeedPackage> packages) => Packages = packages;

    public IReadOnlyList<UpdateFeedPackage> Packages { get; }

    /// <summary>The package with this file name, or null when the feed does not list it.</summary>
    public UpdateFeedPackage? Find(string fileName) =>
        Packages.FirstOrDefault(package => package.FileName.Equals(fileName, StringComparison.Ordinal));

    public static UpdateFeedDocument Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaximumLength)
        {
            throw new UpdateFeedException("The update feed is larger than a feed can be.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new UpdateFeedException($"The update feed is not JSON: {exception.Message}");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !TryGet(document.RootElement, "Assets", out var assets)
                || assets.ValueKind != JsonValueKind.Array)
            {
                throw new UpdateFeedException("The update feed has no list of packages.");
            }

            var packages = new List<UpdateFeedPackage>();
            foreach (var asset in assets.EnumerateArray())
            {
                packages.Add(ReadPackage(asset));
            }

            return new UpdateFeedDocument(packages);
        }
    }

    private static UpdateFeedPackage ReadPackage(JsonElement asset)
    {
        if (asset.ValueKind != JsonValueKind.Object)
        {
            throw new UpdateFeedException("The update feed lists something that is not a package.");
        }

        var fileName = RequiredText(asset, "FileName");
        if (!SafeFileName().IsMatch(fileName) || fileName.Contains("..", StringComparison.Ordinal))
        {
            throw new UpdateFeedException($"The update feed names a file that is not a plain file name: {fileName}");
        }

        var sha256 = RequiredText(asset, "SHA256");
        if (!Sha256Hex().IsMatch(sha256))
        {
            throw new UpdateFeedException($"The update feed gives no usable SHA256 for {fileName}.");
        }

        if (!TryGet(asset, "Size", out var sizeElement)
            || !sizeElement.TryGetInt64(out var size)
            || size <= 0)
        {
            throw new UpdateFeedException($"The update feed gives no size for {fileName}.");
        }

        var type = TryGet(asset, "Type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
            ? typeElement.GetString()
            : null;
        return new UpdateFeedPackage(
            RequiredText(asset, "PackageId"),
            RequiredText(asset, "Version"),
            string.Equals(type, "Full", StringComparison.OrdinalIgnoreCase),
            fileName,
            sha256.ToUpperInvariant(),
            size);
    }

    private static string RequiredText(JsonElement asset, string name) =>
        TryGet(asset, name, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 } text
            ? text
            : throw new UpdateFeedException($"The update feed has a package with no {name}.");

    /// <summary>Case-insensitive, because the feed's casing is the packaging tool's choice, not ours.</summary>
    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,199}$")]
    private static partial Regex SafeFileName();

    [GeneratedRegex("^[0-9A-Fa-f]{64}$")]
    private static partial Regex Sha256Hex();
}
