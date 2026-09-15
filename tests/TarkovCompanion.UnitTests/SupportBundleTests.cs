using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Common;

namespace TarkovCompanion.UnitTests;

public sealed class SupportBundleTests
{
    /// <summary>
    /// The shape of a screenshot name survives; the position in it does not.
    /// </summary>
    /// <remarks>
    /// A name whose shape this build does not recognise yields no position, and that is
    /// invisible from every other angle — the game confirms the screenshot, the folder is
    /// right, the file is there, and the player never appears on anybody's map. The shape is
    /// the whole diagnosis. Where they were standing is nobody's business.
    /// </remarks>
    [Fact]
    public void MaskingKeepsEverySeparatorAndNoCoordinate()
    {
        var masked = SupportBundle.MaskDigits("2026-09-13[20-15]_123.4, 5.6, -78.9_0.0, 0.0, 0.0, 1.0_12.00.png");

        Assert.Equal("0000-00-00[00-00]_000.0, 0.0, -00.0_0.0, 0.0, 0.0, 0.0_00.00.png", masked);
        Assert.DoesNotContain("123", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("78.9", masked, StringComparison.Ordinal);
    }

    /// <summary>
    /// A locale that writes decimals with a comma stays visible after masking.
    /// </summary>
    /// <remarks>
    /// This is the shape most likely to be failing on somebody else's machine, and it would be
    /// worthless if masking flattened it into the shape that works.
    /// </remarks>
    [Fact]
    public void AnUnexpectedShapeIsStillRecognisableAfterMasking()
    {
        var masked = SupportBundle.MaskDigits("2026-09-13[20-15-33]_123,4, 5,6, -78,9_1.2E-05.png");

        Assert.Contains("[00-00-00]", masked, StringComparison.Ordinal);
        Assert.Contains("000,0", masked, StringComparison.Ordinal);
        Assert.Contains("E-00", masked, StringComparison.Ordinal);
    }

    /// <summary>A user's folder name is a real name often enough to matter.</summary>
    [Theory]
    [InlineData(@"C:\Users\Geoffrey\AppData\Local\TarkovCompanion", @"C:\Users\<user>\AppData\Local\TarkovCompanion")]
    [InlineData(@"d:\users\clay\Documents", @"d:\users\<user>\Documents")]
    public void AUserFolderIsReplaced(string line, string expected) =>
        Assert.Equal(expected, SupportBundle.Redact(line));

    /// <summary>
    /// The group key is the only thing protecting a room, and it must never travel in a report.
    /// </summary>
    [Theory]
    [InlineData("X-Group-Key: hunter2hunter2")]
    [InlineData("""sending {"key":"hunter2hunter2"} to the relay""")]
    public void AGroupKeyIsRedacted(string line)
    {
        var redacted = SupportBundle.Redact(line);

        // The invariant is that the secret is gone and the line still reads, not that the
        // punctuation lands in any particular place.
        Assert.DoesNotContain("hunter2hunter2", redacted, StringComparison.Ordinal);
        Assert.Contains("<redacted>", redacted, StringComparison.Ordinal);
    }

    /// <summary>A line that carries neither is left exactly as it was.</summary>
    [Fact]
    public void AnOrdinaryLineIsUntouched()
    {
        const string line = "2026-09-13T21:04:11Z [group] the relay did not answer";

        Assert.Equal(line, SupportBundle.Redact(line));
    }

    /// <summary>
    /// The last line of a report is true of the report above it.
    /// </summary>
    /// <remarks>
    /// It ended "no coordinates are included" while the log tail above it named screenshots in
    /// full, which is how the watcher logs them, and Report a problem sends that text unread.
    /// The first assertion measures the gap that is still open (RISK-REPORT-REDACTION). When #281
    /// filters the whole payload it will fail, and the footer should change in the same commit.
    /// </remarks>
    [Fact]
    public void TheFooterAdmitsTheCoordinatesTheLogTailStillCarries()
    {
        var log = Path.Combine(Path.GetTempPath(), $"support-bundle-{Guid.NewGuid():N}.log");
        File.WriteAllText(
            log,
            "2026-09-13T21:04:11Z Read 2026-09-13[20-15]_123.4, 5.6, -78.9_0.0, 0.0, 0.0, 1.0_12.00.png as Raid with status Found.");
        try
        {
            var snapshot = new RuntimeStateStore(new(
                false,
                Offline: true,
                GameMode.Regular,
                "en",
                TimeSpan.FromHours(9),
                TimeSpan.FromMinutes(5))).Current;

            var report = SupportBundle.Describe(snapshot, [], log);
            var lastLine = report.TrimEnd().Split('\n')[^1];

            Assert.Contains("123.4, 5.6, -78.9", report, StringComparison.Ordinal);
            Assert.EndsWith(SupportBundle.Footer + Environment.NewLine, report, StringComparison.Ordinal);
            Assert.Contains("coordinates", lastLine, StringComparison.Ordinal);
            Assert.DoesNotContain("no coordinates", report, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(log);
        }
    }
}
