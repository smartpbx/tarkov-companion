using SkiaSharp;
using Svg.Skia;
using System.Xml;
using System.Xml.Linq;

namespace TarkovCompanion.Infrastructure.Maps;

internal static class SvgMapRasterizer
{
    private const int MaximumPreviewDimension = 4096;

    public static async Task CreatePreviewAsync(
        string svgPath,
        string previewPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSvg(svgPath);
        var svg = new SKSvg();
        var picture = svg.Load(svgPath)
            ?? throw new InvalidDataException("The cached SVG map could not be rendered.");
        var bounds = picture.CullRect;
        if (!float.IsFinite(bounds.Width) || !float.IsFinite(bounds.Height) || bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new InvalidDataException("The cached SVG map has invalid visual bounds.");
        }

        var scale = Math.Min(
            1d,
            Math.Min(MaximumPreviewDimension / (double)bounds.Width, MaximumPreviewDimension / (double)bounds.Height));
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

    private static void ValidateSvg(string svgPath)
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
    }
}
