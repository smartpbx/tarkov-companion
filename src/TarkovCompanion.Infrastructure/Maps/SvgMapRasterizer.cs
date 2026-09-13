using SkiaSharp;
using Svg.Skia;
using System.Xml;
using System.Xml.Linq;

namespace TarkovCompanion.Infrastructure.Maps;

internal static class SvgMapRasterizer
{
    /// <summary>How large the rasterised map may be along its longer side.</summary>
    /// <remarks>
    /// A budget to render *up to*, not a ceiling to stay under. The distinction is the whole
    /// of this file's history: the scale used to be clamped at 1, which treated the SVG as
    /// though enlarging it would interpolate. It is a vector. Rendering it larger produces
    /// genuinely more detail, and rendering it at its intrinsic size produces whatever the
    /// author happened to type into the viewBox.
    ///
    /// For Factory that was 130.8 by 141.2 — a hundred and thirty pixels, stretched across the
    /// whole map panel, which is what "the factory map drawing is super low res" was.
    /// </remarks>
    private const int MaximumPreviewDimension = 4096;

    public static Task CreatePreviewAsync(
        string svgPath,
        string previewPath,
        CancellationToken cancellationToken) =>
        CreatePreviewAsync(svgPath, previewPath, visibleLayer: null, cancellationToken);

    public static async Task CreatePreviewAsync(
        string svgPath,
        string previewPath,
        string? visibleLayer,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var document = LoadAndValidateSvg(svgPath);
        SelectVisibleLayer(document, visibleLayer);
        using var svgStream = new MemoryStream();
        document.Save(svgStream, SaveOptions.DisableFormatting);
        svgStream.Position = 0;
        var svg = new SKSvg();
        var picture = svg.Load(svgStream)
            ?? throw new InvalidDataException("The cached SVG map could not be rendered.");
        var bounds = picture.CullRect;
        if (!float.IsFinite(bounds.Width) || !float.IsFinite(bounds.Height) || bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new InvalidDataException("The cached SVG map has invalid visual bounds.");
        }

        // Fills the budget in both directions rather than refusing to grow. Small maps gain
        // the most: Factory's viewBox is 130.8 x 141.2, so this is the difference between a
        // 131-pixel image and a 3795-pixel one, from the same file.
        var scale = Math.Min(
            MaximumPreviewDimension / (double)bounds.Width,
            MaximumPreviewDimension / (double)bounds.Height);
        var width = Math.Max(1, (int)Math.Ceiling(bounds.Width * scale));
        var height = Math.Max(1, (int)Math.Ceiling(bounds.Height * scale));
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("A map preview surface could not be created.");
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.Scale((float)scale);
        surface.Canvas.Translate(-bounds.Left, -bounds.Top);
        surface.Canvas.DrawPicture(picture);
        surface.Canvas.Flush();
        cancellationToken.ThrowIfCancellationRequested();

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("The SVG map preview could not be encoded.");
        await using var output = new FileStream(
            previewPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await data.AsStream().CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }

    private static XDocument LoadAndValidateSvg(string svgPath)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 32L * 1024 * 1024,
        };
        using var reader = XmlReader.Create(svgPath, settings);
        var document = XDocument.Load(reader, LoadOptions.None);
        var root = document.Root;
        if (root is null || !string.Equals(root.Name.LocalName, "svg", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The cached SVG map has no SVG root element.");
        }

        var externalReference = root.DescendantsAndSelf()
            .Attributes()
            .FirstOrDefault(attribute =>
                attribute.Name.LocalName is "href" or "src" &&
                !string.IsNullOrWhiteSpace(attribute.Value) &&
                !attribute.Value.StartsWith('#'));
        var externalStyle = document.Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "style", StringComparison.OrdinalIgnoreCase))
            .Any(element =>
                element.Value.Contains("@import", StringComparison.OrdinalIgnoreCase) ||
                element.Value.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
                element.Value.Contains("https://", StringComparison.OrdinalIgnoreCase) ||
                element.Value.Contains("file:", StringComparison.OrdinalIgnoreCase));
        if (externalReference is not null || externalStyle)
        {
            throw new InvalidDataException("The cached SVG map contains an external resource reference.");
        }

        return document;
    }

    private static void SelectVisibleLayer(XDocument document, string? visibleLayer)
    {
        if (string.IsNullOrWhiteSpace(visibleLayer))
        {
            return;
        }

        var layerGroups = document.Root!
            .Elements()
            .Where(element =>
                string.Equals(element.Name.LocalName, "g", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(element.Attribute("id")?.Value))
            .ToArray();
        if (!layerGroups.Any(group =>
                string.Equals(group.Attribute("id")?.Value, visibleLayer, StringComparison.Ordinal)))
        {
            throw new InvalidDataException($"The cached SVG map does not contain upstream layer '{visibleLayer}'.");
        }

        foreach (var group in layerGroups)
        {
            var keepWithGroup = group.Attributes().FirstOrDefault(attribute =>
                string.Equals(attribute.Name.LocalName, "data-keep-with-group", StringComparison.OrdinalIgnoreCase));
            if (!string.Equals(group.Attribute("id")?.Value, visibleLayer, StringComparison.Ordinal) &&
                !string.Equals(keepWithGroup?.Value, visibleLayer, StringComparison.Ordinal))
            {
                group.Remove();
            }
        }
    }
}
