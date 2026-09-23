using System.Text.Json;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Infrastructure.Recognition;

namespace TarkovCompanion.RecognitionTests;

internal sealed record SyntheticScene(
    string Name,
    string Source,
    int Width,
    int Height,
    double Scale,
    double Noise,
    string ExpectedContext,
    IReadOnlyList<SyntheticLine> Lines)
{
    public FixtureOcrScene ToOcrScene() => new(
        Source,
        Lines.Select(line => new OcrLine(
            line.Text,
            new PixelRect(line.X, line.Y, line.Width, line.Height),
            new Confidence(line.Confidence))).ToArray());

    public CapturedImage CreateImage()
    {
        var pixels = new byte[checked(Width * Height)];
        Array.Fill(pixels, (byte)24);
        var interval = Math.Max(17, (int)Math.Round(1 / Math.Max(Noise, 0.001)));
        for (var index = interval; index < pixels.Length; index += interval)
        {
            pixels[index] = (byte)(18 + (index % 13));
        }

        return new(
            pixels,
            Width,
            Height,
            Width,
            PixelFormat.Gray8,
            new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero),
            Source);
    }
}

internal sealed record SyntheticLine(
    string Text,
    int X,
    int Y,
    int Width,
    int Height,
    double Confidence);

internal static class SyntheticFixtureLoader
{
    public static IReadOnlyList<SyntheticScene> LoadScenes()
    {
        var root = FindRepositoryRoot();
        return Load(root, "synthetic-scenes.json");
    }

    public static IReadOnlyList<SyntheticScene> LoadTaskContextScenes()
    {
        var root = FindRepositoryRoot();
        return Load(root, "tasks-context-scenes.json");
    }

    private static IReadOnlyList<SyntheticScene> Load(string root, string name)
    {
        var json = File.ReadAllText(Path.Combine(root, "fixtures", "recognition", name));
        return JsonSerializer.Deserialize<IReadOnlyList<SyntheticScene>>(
                   json,
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidDataException("Synthetic recognition fixture was empty.");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TarkovCompanion.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Tarkov Companion repository root.");
    }
}
