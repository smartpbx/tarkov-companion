using TarkovCompanion.Application.Services;

namespace TarkovCompanion.UnitTests;

public sealed class ScreenshotFilenameParserTests
{
    [Fact]
    public void ParsesObservedFilenameConvention()
    {
        var parser = new ScreenshotFilenameParser();

        var result = parser.TryParse(
            "2026-09-04[18-33]_7.86, 38.06, -27.57_-0.03307, -0.13322, 0.00384, -0.99053_21.87 (0).png",
            TimeSpan.FromHours(-4),
            out var position);

        Assert.True(result);
        Assert.NotNull(position);
        Assert.Equal(7.86, position.Position.X, 3);
        Assert.Equal(38.06, position.Position.Y, 3);
        Assert.Equal(-27.57, position.Position.Z, 3);
        Assert.NotNull(position.InGameTime);
        Assert.Equal(21.87, position.InGameTime.Value.TotalSeconds, 2);
        Assert.Equal(0, position.DuplicateIndex);
        Assert.InRange(position.HeadingDegrees, 0, 360);
    }

    [Theory]
    [InlineData("screenshot.png")]
    [InlineData("2026-99-99[18-33]_1, 2, 3_0, 0, 0, 1.png")]
    [InlineData("2026-09-04[18-33]_1, 2, 3_0, 0, 0, 0.png")]
    [InlineData("2026-09-04[18-33]_1, 2, 3_0, 0, 0, 1.exe")]
    public void RejectsMalformedOrUnsafeNames(string filename)
    {
        var parser = new ScreenshotFilenameParser();

        Assert.False(parser.TryParse(filename, TimeSpan.Zero, out _));
    }

    /// <summary>
    /// A menu or hideout screenshot has no coordinates in its name, which is ordinary.
    /// </summary>
    [Fact]
    public void ReportsNoPositionForAScreenshotTakenOutsideARaid()
    {
        var parser = new ScreenshotFilenameParser();

        Assert.False(parser.TryParse("2024-02-08[22-19] (0).png", TimeSpan.FromHours(-4), out var position));
        Assert.Null(position);
    }

    /// <summary>
    /// The capture time stays with the coordinate-bearing filename even when mtime differs.
    /// </summary>
    /// <remarks>
    /// A filesystem stamp is mutable metadata, not screenshot identity. A future-dated mtime
    /// caused every later position to be rejected after the workstation clock was corrected.
    /// </remarks>
    [Fact]
    public void TakesCaptureTimeAndCoordinatesFromTheName()
    {
        var parser = new ScreenshotFilenameParser();
        var directory = Directory.CreateTempSubdirectory("tarkov-screenshot-time");
        try
        {
            var path = Path.Combine(
                directory.FullName,
                "2026-09-11[19-16]_80.02, 1.39, -51.06_-0.00242, 0.84404, 0.00393, 0.53626_9.91 (0).png");
            File.WriteAllBytes(path, [0]);
            var written = new DateTime(2026, 9, 11, 23, 16, 42, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(path, written);

            Assert.True(parser.TryParseFile(path, TimeSpan.FromHours(-4), out var position));
            Assert.NotNull(position);
            Assert.Equal(
                new DateTimeOffset(2026, 9, 11, 19, 16, 0, TimeSpan.FromHours(-4)),
                position.Timestamp);
            Assert.Equal(80.02, position.Position.X, 3);
            Assert.Equal(-51.06, position.Position.Z, 3);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// With no file to ask, the name's own time is the only one there is.
    /// </summary>
    [Fact]
    public void FallsBackToTheNameWhenTheFileIsGone()
    {
        var parser = new ScreenshotFilenameParser();
        var path = Path.Combine(
            Path.GetTempPath(),
            "tarkov-companion-absent",
            "2026-09-04[18-33]_7.86, 38.06, -27.57_-0.03307, -0.13322, 0.00384, -0.99053_21.87 (0).png");

        Assert.True(parser.TryParseFile(path, TimeSpan.FromHours(-4), out var position));
        Assert.NotNull(position);
        Assert.Equal(new DateTimeOffset(2026, 9, 4, 18, 33, 0, TimeSpan.FromHours(-4)), position.Timestamp);
    }

    [Fact]
    public void ConvertsKnownQuarterTurnToHeading()
    {
        var halfSqrt = Math.Sqrt(0.5);
        var heading = ScreenshotFilenameParser.HeadingDegrees(new(0, halfSqrt, 0, halfSqrt));

        Assert.Equal(90, heading, 6);
    }

    /// <summary>
    /// A OneDrive-synced folder can rewrite a file's write time to whenever it synced. Read at
    /// 05:37Z off a name naming 21:37 local (-8) on a live installation, the write time was
    /// eight hours away; the immutable name still says when the player requested the capture.
    /// </summary>
    [Fact]
    public void KeepsTheNamesTimeWhenTheFilesTimeDisagreesByHours()
    {
        var parser = new ScreenshotFilenameParser();
        var directory = Directory.CreateTempSubdirectory("tarkov-screenshot-drift");
        try
        {
            var path = Path.Combine(
                directory.FullName,
                "2026-09-16[21-37]_80.02, 1.39, -51.06_-0.00242, 0.84404, 0.00393, 0.53626_9.91 (0).png");
            File.WriteAllBytes(path, [0]);
            // The name says 2026-09-17T05:37:00Z (21:37 local at -8); a sync artifact eight
            // hours later is nothing like a filesystem stamping the file it just wrote.
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 9, 17, 13, 37, 0, DateTimeKind.Utc));

            Assert.True(parser.TryParseFile(path, TimeSpan.FromHours(-8), out var position));
            Assert.NotNull(position);
            Assert.Equal(new DateTimeOffset(2026, 9, 17, 5, 37, 0, TimeSpan.Zero), position.Timestamp);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Neither clock is allowed to place a screenshot in the future: it would sit ahead of
    /// every real position that follows it and hold the raid's "last seen" time ahead of now
    /// until real time caught up.
    /// </summary>
    [Fact]
    public void NeverReportsAPositionInTheFuture()
    {
        var now = new DateTimeOffset(2026, 9, 17, 2, 25, 0, TimeSpan.Zero);
        var parser = new ScreenshotFilenameParser(new FakeTimeProvider(now));
        var directory = Directory.CreateTempSubdirectory("tarkov-screenshot-future");
        try
        {
            var path = Path.Combine(
                directory.FullName,
                "2026-09-16[21-37]_80.02, 1.39, -51.06_-0.00242, 0.84404, 0.00393, 0.53626_9.91 (0).png");
            File.WriteAllBytes(path, [0]);
            // The filesystem time is irrelevant; the name itself is three hours ahead of "now".
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 9, 17, 5, 37, 34, DateTimeKind.Utc));

            Assert.True(parser.TryParseFile(path, TimeSpan.FromHours(-8), out var position));
            Assert.NotNull(position);
            Assert.Equal(now, position.Timestamp);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
